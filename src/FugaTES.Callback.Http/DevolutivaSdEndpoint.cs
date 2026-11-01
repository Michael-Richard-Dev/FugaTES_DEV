using System.Buffers;
using System.Text;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using FugaTES.Aplicacao.Servicos.Sd;

namespace FugaTES.Callback.Http;

/// <summary>
/// Recebe do Guardian a pesagem do fluxo de VENDA.
///
/// Rota propria, separada de /recebimento: aquela esta em producao para o ACM e tem admissao e
/// correlacao proprias. Um defeito no fluxo novo nao pode derrubar o que ja funciona.
///
/// Aqui so se recebe, correlaciona e grava. Nenhuma chamada ao SAP: a criacao da remessa e etapa
/// seguinte e depende de decisoes que ainda nao estao fechadas.
/// </summary>
public static class DevolutivaSdEndpoint
{
    public const string Route = "/api/guardian/set26/callback/venda";
    public const int MaxBytes = 1048576;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(Route, (HttpContext context) => HandleAsync(context));
        // GET/HEAD respondem para quem precisa so confirmar que a rota existe, sem gravar nada.
        endpoints.MapMethods(Route, ["GET", "HEAD"], (HttpContext context) =>
            context.Response.WriteAsJsonAsync(
                new { status = "RECEIVER_READY", probe = true, persisted = false, method = "POST", endpoint = Route },
                context.RequestAborted));
    }

    public static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.HasJsonContentType())
        { await ResponderAsync(context, 415, "INVALID_CONTENT_TYPE"); return; }
        if (context.Request.ContentLength > MaxBytes)
        { await ResponderAsync(context, 413, "CALLBACK_TOO_LARGE"); return; }

        string corpo;
        try { corpo = await LerCorpoAsync(context); }
        catch (InvalidOperationException) { await ResponderAsync(context, 413, "CALLBACK_TOO_LARGE"); return; }

        DevolutivaPesagemSd devolutiva;
        try { devolutiva = LeitorDevolutivaSd.Ler(corpo); }
        // O motivo volta para o Guardian: uma recusa sem causa nao ajuda ninguem a corrigir.
        catch (DevolutivaSdInvalidaException erro)
        {
            // E tambem fica no journal. Em 01/11/2026 duas recusas chegaram e so o Guardian sabia
            // o porque: do nosso lado havia um 400 sem causa. O CODIGO e o detalhe bastam para
            // diagnosticar, e nenhum dos dois carrega o documento recebido.
            context.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(DevolutivaSdEndpoint))
                .LogWarning("SD_DEVOLUTIVA_RECUSADA: {Codigo} — {Detalhe}", erro.Codigo, erro.Detalhe);
            await ResponderAsync(context, 400, erro.Codigo, erro.Detalhe);
            return;
        }

        var repositorio = context.RequestServices.GetService<IDevolutivasSdRepositorio>();
        if (repositorio is null)
        { await ResponderAsync(context, 503, "SD_RECEPTOR_NAO_CONFIGURADO"); return; }

        try
        {
            await repositorio.RegistrarAsync(devolutiva, context.RequestAborted);
        }
        catch (DevolutivaSdJaRecebidaException)
        {
            // Reenvio do mesmo evento NAO e erro: o Guardian repete quando nao tem certeza de que
            // chegou. Responder 200 evita que ele fique reenviando para sempre, e o 409 faria
            // parecer falha. Nada e gravado de novo.
            await context.Response.WriteAsJsonAsync(new
            {
                status = "JA_RECEBIDA",
                idPesagem = devolutiva.IdPesagem,
                persisted = false
            }, context.RequestAborted);
            return;
        }

        await context.Response.WriteAsJsonAsync(new
        {
            status = "RECEBIDA",
            idPesagem = devolutiva.IdPesagem,
            persisted = true,
            // Dito em voz alta na resposta: sem correlacao a pesagem entrou, mas ninguem sabe
            // ainda de qual ordem ela e.
            correlacao = devolutiva.TemCorrelacao
                ? EstadoDevolutivaSd.Correlacionada
                : EstadoDevolutivaSd.SemCorrelacao,
            chave = devolutiva.ChaveNatural
        }, context.RequestAborted);
    }

    // Leitura limitada: o tamanho declarado pode mentir, entao o teto vale tambem durante a
    // leitura, e nao so no cabecalho.
    private static async Task<string> LerCorpoAsync(HttpContext context)
    {
        using var destino = new MemoryStream();
        var pedaco = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int lidos;
            while ((lidos = await context.Request.Body.ReadAsync(pedaco.AsMemory(0, 8192), context.RequestAborted)) > 0)
            {
                if (destino.Length + lidos > MaxBytes) throw new InvalidOperationException("CALLBACK_TOO_LARGE");
                destino.Write(pedaco, 0, lidos);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(pedaco, clearArray: true); }
        return Encoding.UTF8.GetString(destino.ToArray());
    }

    private static Task ResponderAsync(HttpContext context, int status, string codigo, string? detalhe = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            new { status = "RECUSADA", codigo, detalhe, persisted = false }, context.RequestAborted);
    }
}
