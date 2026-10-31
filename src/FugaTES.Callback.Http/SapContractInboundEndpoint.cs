using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;

namespace FugaTES.Callback.Http;

public static class SapContractInboundEndpoint
{
    public const string Route = "/api/v1/integracoes/sap/contratos";
    private const string UsernameKey = "SapInbound:BasicAuth:Username";
    private const string PasswordKey = "SapInbound:BasicAuth:Password";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(Route, HandleAsync);
    }

    public static void MapDurable(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(Route, HandleDurableAsync);
    }

    public static async Task HandleDurableAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, new { mensagem = "Acesso negado." });
            return;
        }
        if (!IsAuthorized(context))
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, new { mensagem = "Acesso negado." });
            return;
        }
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        if (configuration["SapContractInbound:SecurityMode"] == "LoopbackBasicHttps" &&
            (context.Request.Headers["X-Forwarded-Proto"].Count != 1 ||
             context.Request.Headers["X-Forwarded-Proto"][0] != "https"))
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, new { mensagem = "Acesso negado." });
            return;
        }
        if (!context.Request.HasJsonContentType())
        {
            await WriteAsync(context, StatusCodes.Status415UnsupportedMediaType, new { mensagem = "Content-Type inválido." });
            return;
        }

        try
        {
            var dto = await context.Request.ReadFromJsonAsync<SapContractInboundHttpDto>(context.RequestAborted)
                ?? throw new ArgumentException("Request body is required.");
            if (dto.Contrato?.Count != 1 || dto.Contrato.Single() is null)
                throw new ArgumentException("Contrato collection must contain exactly one item.");

            var snapshot = SapContractInboundMapper.MapearSnapshot(dto.Contrato.Single()!);
            var automaticText = configuration["SapContractInbound:AutomaticProcessingEnabled"];
            if (automaticText is not null && !bool.TryParse(automaticText, out _))
            {
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, new { mensagem = "SAP_INBOUND_SCOPE_UNAVAILABLE" });
                return;
            }
            var automatic = bool.TryParse(automaticText, out var enabledAutomatic) && enabledAutomatic;
            var windowText = configuration["MotorIntegracao:AcmInboundWindow:Enabled"];
            if (windowText is not null && !bool.TryParse(windowText, out _))
            {
                await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, new { mensagem = "SAP_INBOUND_SCOPE_UNAVAILABLE" });
                return;
            }
            AcmInboundWindowOptions? window = null;
            if (automatic || bool.TryParse(windowText, out var windowEnabled) && windowEnabled)
            {
                MotorIntegracaoOptions? scope = null;
                try { scope = configuration.GetSection("MotorIntegracao").Get<MotorIntegracaoOptions>(); }
                catch (InvalidOperationException) { /* Invalid configuration never authorizes persistence. */ }
                if (scope is null || !scope.EhValida() ||
                    (!scope.UsaEscopoAcm && !scope.AcmInboundWindow.Enabled))
                {
                    await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, new { mensagem = "SAP_INBOUND_SCOPE_UNAVAILABLE" });
                    return;
                }
                if (scope.AcmInboundWindow.Enabled)
                {
                    window = scope.AcmInboundWindow;
                    if (!automatic || configuration["SapContractInbound:SecurityMode"] != "LoopbackBasicHttps")
                    {
                        await WriteAsync(context, StatusCodes.Status503ServiceUnavailable, new { mensagem = "SAP_INBOUND_SCOPE_UNAVAILABLE" });
                        return;
                    }
                    var clock = context.RequestServices.GetRequiredService<TimeProvider>();
                    if (!window.EstaAberta(clock.GetUtcNow()))
                    {
                        await WriteAsync(context, StatusCodes.Status403Forbidden, new { mensagem = "SAP_INBOUND_WINDOW_CLOSED" });
                        return;
                    }
                    if (!window.SnapshotPermitido(snapshot))
                    {
                        await WriteAsync(context, StatusCodes.Status422UnprocessableEntity, new { mensagem = "SAP_INBOUND_SCOPE_NOT_AUTHORIZED" });
                        return;
                    }
                }
                else if (snapshot.NumeroContrato != scope.AcmPilotNumeroContrato || snapshot.Centro != scope.AcmPilotCentro ||
                         snapshot.Item != scope.AcmPilotItem)
                {
                    await WriteAsync(context, StatusCodes.Status422UnprocessableEntity, new { mensagem = "SAP_INBOUND_SCOPE_NOT_AUTHORIZED" });
                    return;
                }
            }
            var correlationId = window?.CriarCorrelationId() ?? Guid.NewGuid().ToString("N");
            var useCase = context.RequestServices.GetRequiredService<AceitarContratoSapToledoDuravelCasoDeUso>();
            var result = window is null
                ? await useCase.ExecutarAsync(snapshot, correlationId, context.RequestAborted)
                : await useCase.ExecutarAdmitidoAsync(snapshot, correlationId, window.CriarAdmissao(), context.RequestAborted);
            await WriteAsync(context, StatusCodes.Status201Created, new
            {
                resultado = result.Reentrega ? "REENTREGA_IDEMPOTENTE" : "ACEITO",
                numeroContrato = result.NumeroContrato,
                versao = result.Versao,
                reentrega = result.Reentrega,
                correlationId = result.CorrelationId
            });
        }
        catch (AcmInboundScopeConflictException)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, new { mensagem = "SAP_INBOUND_ADMISSION_CONFLICT" });
        }
        catch (JsonException)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, new { mensagem = "JSON inválido." });
        }
        catch (ArgumentException)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, new { mensagem = "Requisição inválida." });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, new { mensagem = "Erro interno." });
        }
    }

    public static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            if (!IsAuthorized(context))
            {
                await WriteAsync(context, StatusCodes.Status401Unauthorized, new { mensagem = "Não autorizado." }).ConfigureAwait(false);
                return;
            }

            var dto = await context.Request
                .ReadFromJsonAsync<SapContractInboundHttpDto>(context.RequestAborted)
                .ConfigureAwait(false)
                ?? throw new ArgumentException("Request body is required.");
            if (dto.Contrato?.Count != 1)
                throw new ArgumentException("Contrato collection must contain exactly one item.");
            var snapshot = SapContractInboundMapper.MapearSnapshot(dto.Contrato.Single()!);
            var useCase = context.RequestServices.GetRequiredService<ProcessarContratoSapToledoIdempotenteCasoDeUso>();
            var resultado = await useCase.ExecutarAsync(snapshot, Guid.NewGuid().ToString("N"), context.RequestAborted).ConfigureAwait(false);
            if (resultado == ResultadoContratoSapToledo.ValidacaoConhecida)
            {
                await WriteBusinessErrorAsync(context, "processamento rejeitado.").ConfigureAwait(false);
                return;
            }
            if (resultado is not (ResultadoContratoSapToledo.Concluida or ResultadoContratoSapToledo.IdempotenteConcluida))
            { await WriteAsync(context, StatusCodes.Status500InternalServerError, new { mensagem = "Erro interno." }).ConfigureAwait(false); return; }

            await WriteAsync(context, StatusCodes.Status201Created, new { mensagem = "Sucesso." }).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteBusinessErrorAsync(context, "JSON inválido.").ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await WriteBusinessErrorAsync(context, "requisição inválida.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteAsync(context, StatusCodes.Status500InternalServerError, new { mensagem = "Erro interno." }).ConfigureAwait(false);
        }
    }

    private static bool IsAuthorized(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var expectedUsername = configuration[UsernameKey];
        var expectedPassword = configuration[PasswordKey];
        if (string.IsNullOrEmpty(expectedUsername) || string.IsNullOrEmpty(expectedPassword)) return false;
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter)) return false;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            var separator = decoded.IndexOf(':');
            if (separator < 0) return false;
            return FixedTimeEquals(decoded[..separator], expectedUsername) &&
                   FixedTimeEquals(decoded[(separator + 1)..], expectedPassword);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static Task WriteBusinessErrorAsync(HttpContext context, string reason) =>
        WriteAsync(context, StatusCodes.Status400BadRequest, new { mensagem = $"Erro: {reason}" });

    private static async Task WriteAsync(HttpContext context, int statusCode, object payload)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }
}
