using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Dtos.BusinessPartner;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Integracao.SAP.Mapeamentos;
using System.Net;
using System.Text.Json;

namespace FugaTES.Integracao.SAP.BusinessPartner;

public abstract class ConsultaParceiroNegocioSapAdapterBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ClienteHttpParceiroNegocioSap clienteHttp;
    private readonly MapeadorParceiroNegocioSap mapeador;

    protected ConsultaParceiroNegocioSapAdapterBase(ClienteHttpParceiroNegocioSap clienteHttp, TimeProvider timeProvider)
    {
        this.clienteHttp = clienteHttp ?? throw new ArgumentNullException(nameof(clienteHttp));
        mapeador = new MapeadorParceiroNegocioSap(timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
    }

    protected async Task<ResultadoConsultaSap<ParceiroNegocioConsultado>> ConsultarAsync(
        string businessPartner,
        CategoriaParceiroNegocio categoria,
        CancellationToken cancellationToken)
    {
        try
        {
            var resposta = await clienteHttp.ObterAsync(businessPartner, cancellationToken, solicitarJson: categoria == CategoriaParceiroNegocio.Transportadora).ConfigureAwait(false);
            if (resposta.FalhaConfiguracao is not null)
            {
                return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(resposta.FalhaConfiguracao.Value);
            }

            return resposta.StatusCode switch
            {
                HttpStatusCode.OK => MapearConteudo(resposta.Conteudo, categoria),
                HttpStatusCode.BadRequest => ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaContrato),
                HttpStatusCode.Unauthorized => ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaAutenticacao),
                HttpStatusCode.Forbidden => ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaAutorizacao),
                HttpStatusCode.NotFound => ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.NaoEncontrado),
                >= HttpStatusCode.InternalServerError and <= (HttpStatusCode)599 => ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaTecnica),
                _ => ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaTecnica)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaTecnica);
        }
        catch (HttpRequestException)
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaTecnica);
        }
        catch (JsonException)
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaContrato);
        }
    }

    private ResultadoConsultaSap<ParceiroNegocioConsultado> MapearConteudo(string? conteudo, CategoriaParceiroNegocio categoria)
    {
        if (string.IsNullOrWhiteSpace(conteudo))
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaContrato);
        }

        // Preserve the legacy model; SAP's real carrier response represents this flag as Boolean.
        if (categoria == CategoriaParceiroNegocio.Transportadora &&
            System.Text.Json.Nodes.JsonNode.Parse(conteudo) is System.Text.Json.Nodes.JsonObject root &&
            root["d"] is System.Text.Json.Nodes.JsonObject partner &&
            partner["BusinessPartnerIsBlocked"] is System.Text.Json.Nodes.JsonValue flag &&
            flag.TryGetValue<bool>(out var blocked))
        {
            partner["BusinessPartnerIsBlocked"] = blocked ? "true" : "false";
            conteudo = root.ToJsonString();
        }

        var dto = JsonSerializer.Deserialize<RespostaODataV2Dto>(conteudo, JsonOptions);
        if (dto?.D is null)
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaContrato);
        }

        return mapeador.Mapear(dto.D, categoria);
    }
}
