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
                // "mensagem" tambem aqui: quem testa a rota pelo Guardian ve OK em vez de erro.
                new { mensagem = MensagemSucesso, status = "RECEIVER_READY", probe = true,
                      persisted = false, method = "POST", endpoint = Route },
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

        // Pesagem com contrato do ACM nao e nossa. O Guardian manda a MESMA finalizacao para as
        // duas rotas, e ate 03/11/2026 nos guardavamos as duas: a 0020344 virou uma linha de
        // venda em branco, sem ordem e sem nada a fazer.
        //
        // Nao e recusa - nada esta errado com o documento, ele so pertence ao outro fluxo. Por
        // isso 200 com "mensagem": OK: uma recusa faria o Guardian marcar falha e reenviar para
        // sempre uma pesagem que esta certa. E nada e gravado.
        if (devolutiva.ContratoAcm is not null)
        {
            // Warning, e nao Information, por um motivo pratico: o servico sobe com
            // "--Logging:LogLevel:FugaTES.Callback.Http.DevolutivaSdEndpoint=Warning" na linha de
            // comando, entao Information nao apareceria no journal. Uma pesagem desviada em
            // silencio e exatamente o que custou a manha de 03/11/2026 a ser diagnosticado.
            //
            // O numero do contrato NAO vai para o log: para saber que a pesagem foi desviada
            // basta o id dela, e o documento inteiro ja esta do lado do ACM.
            context.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(DevolutivaSdEndpoint))
                .LogWarning("SD_DEVOLUTIVA_E_DO_ACM: {Pesagem} tem contrato ACM; nada gravado.",
                    devolutiva.IdPesagem);
            await context.Response.WriteAsJsonAsync(new
            {
                mensagem = MensagemSucesso,
                status = "NAO_E_DO_SD",
                idPesagem = devolutiva.IdPesagem,
                persisted = false,
                detalhe = "Pesagem com contrato do ACM: pertence ao fluxo de recebimento."
            }, context.RequestAborted);
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
                // Reenvio e sucesso do ponto de vista do Guardian: nao ha o que reenviar de novo.
                mensagem = MensagemSucesso,
                status = "JA_RECEBIDA",
                idPesagem = devolutiva.IdPesagem,
                persisted = false
            }, context.RequestAborted);
            return;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        // Qualquer outra falha ao gravar. Sem isto a excecao escapava e virava um 500 nu: sem
        // linha no journal, sem campo "mensagem" para o Guardian, sem causa para ninguem. Foi o
        // que aconteceu com a pesagem 0020341 em 02/11/2026, quando a correlacao pela metade
        // violou a regra da tabela. O tipo da excecao basta para diagnosticar e nao carrega o
        // documento recebido. Nao registrar a excecao inteira: detalhes do banco podem conter dados.
        catch (Exception erro)
        {
            context.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(DevolutivaSdEndpoint))
                .LogError("SD_DEVOLUTIVA_NAO_GRAVADA: {Pesagem} — {Tipo}",
                    devolutiva.IdPesagem, erro.GetType().Name);
            // A conexao pode falhar depois do commit. Sem confirmacao, nao afirmar que o
            // documento nao foi gravado nem autorizar reenvio sem consulta. A chave idempotente
            // continua protegendo o mesmo evento; o JSON original nunca e reescrito.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                mensagem = "SD_DEVOLUTIVA_NAO_GRAVADA",
                status = "RESULTADO_INDETERMINADO",
                codigo = "SD_DEVOLUTIVA_NAO_GRAVADA",
                detalhe = "Não foi possível confirmar a gravação. Consulte o recebimento antes de reenviar.",
                persisted = (bool?)null
            }, context.RequestAborted);
            return;
        }

        await context.Response.WriteAsJsonAsync(new
        {
            // Primeiro campo de proposito: e o unico que o Guardian le. A integracao dele tem
            // "Campo de Retorno do Metodo = mensagem" e "Valor em Caso de Sucesso = OK"
            // (conferido na tela de configuracao em 02/11/2026). Sem ele, o Guardian marca falha
            // mesmo com HTTP 200 e corpo valido - erro 309087, "o campo configurado como result
            // nao foi encontrado" - e fica reenviando para sempre.
            mensagem = MensagemSucesso,
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

    /// <summary>
    /// O que o Guardian exige no campo "mensagem" para considerar a chamada bem-sucedida. Nao e
    /// escolha nossa: esta na configuracao da integracao dele, em "Valor do Retorno do Metodo em
    /// Caso de Sucesso".
    /// </summary>
    public const string MensagemSucesso = "OK";

    private static Task ResponderAsync(HttpContext context, int status, string codigo, string? detalhe = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            // Na recusa, "mensagem" leva o CODIGO - diferente de "OK", que e o que faz o Guardian
            // marcar falha, e com a causa visivel no painel dele em vez de um erro generico.
            new { mensagem = codigo, status = "RECUSADA", codigo, detalhe, persisted = false },
            context.RequestAborted);
    }
}
