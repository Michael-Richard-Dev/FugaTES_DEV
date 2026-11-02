using System.Net;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Persistencia.Sd;
using FugaTES.WinForms.Runtime;
using FugaTES.WinForms.Transportadoras;
using Microsoft.Extensions.Configuration;

namespace FugaTES.WinForms.Sd;

/// <summary>
/// Monta a tela de devolutivas e, com ela, o passo 3 (criar a remessa no SAP).
///
/// Reusa a mesma conexao do fluxo SD (cluster 5433): e a unica rota configurada do desktop ate
/// aquele banco, e as migracoes 007S2/007S3 concedem a fugates_sd_app o que cada tabela exige -
/// SELECT nas devolutivas, SELECT/INSERT/UPDATE nas remessas.
///
/// O lado do SAP e opcional: sem perfil valido a tela abre so com o acompanhamento, com a coluna
/// da remessa marcada como desconhecida, em vez de nao abrir.
/// </summary>
public static class DevolutivasSdComposition
{
    private sealed class Descartavel(params IDisposable?[] itens) : IDisposable
    {
        public void Dispose() { foreach (var i in itens) i?.Dispose(); }
    }

    public static FrmDevolutivasSd Criar()
        => Montar(ConexaoSdPostgreSql.AbrirAsync(CancellationToken.None).GetAwaiter().GetResult());

    /// <summary>
    /// Versao assincrona, para quem abre a tela a partir da interface: abrir a conexao bloqueando
    /// a thread da interface trava o aplicativo enquanto o tunel SSH sobe.
    /// </summary>
    public static async Task<FrmDevolutivasSd> CriarAsync(CancellationToken ct)
        => Montar(await ConexaoSdPostgreSql.AbrirAsync(ct).ConfigureAwait(true));

    private static FrmDevolutivasSd Montar(ConexaoSdPostgreSql.Resultado aberta)
    {
        var (sap, setores, ajuste, textos, expedicao, httpSap, motivoSap) = CriarSap();
        var posse = new Descartavel(aberta.Posse, httpSap);

        if (aberta.Fonte is null)
            return new FrmDevolutivasSd(null, posse, aberta.Motivo);

        return new FrmDevolutivasSd(
            new DevolutivasSdPostgreSql(aberta.Fonte), posse,
            motivoIndisponivel: motivoSap,
            remessas: new RemessasSdPostgreSql(aberta.Fonte),
            sap: sap, setores: setores, ajuste: ajuste, textos: textos, expedicao: expedicao);
    }

    /// <summary>
    /// Executor do SAP para os passos 3 e 4. Nulo quando o perfil do desktop nao fecha: a tela
    /// continua acompanhando as devolutivas, apenas sem criar nem ajustar remessa.
    /// </summary>
    private static (ICriarRemessaSdSap?, IConsultarSetorAtividadeSd?, IAjustarRemessaSdSap?,
        IGravarTextoNfeSd?, IExpedirRemessaSdSap?, IDisposable?, string?) CriarSap()
    {
        HttpClient? http = null;
        try
        {
            var config = CarrierDesktopProfile.Load(
                Path.Combine(AppContext.BaseDirectory, "FugaTES.carrier.json"));
            OrdensVendaSdComposition.Validar(config);
            http = new HttpClient(new RemessaSdHttpGuard(new HttpClientHandler { AllowAutoRedirect = false }));
            var executor = new SapODataHttpExecutor(
                http,
                new OpcoesSap
                {
                    BaseUrl = new Uri(OrdensVendaSdComposition.OrigemAprovada),
                    SapClient = OrdensVendaSdComposition.SapClientAprovado,
                    Timeout = TimeSpan.FromSeconds(60)
                },
                new CarrierServiceSapAuthentication(config),
                // Container proprio: o CSRF so vale junto dos cookies da mesma sessao.
                new CookieContainer());
            return (new RemessaSdSapClient(executor), new SetorAtividadeSdConsulta(executor),
                new AjusteRemessaSdSapClient(executor), new TextoNfeSdSapClient(executor),
                new ExpedicaoSdSapClient(executor), http, null);
        }
        catch (DesktopStartupException ex)
        { http?.Dispose(); return (null, null, null, null, null, null, ex.Code); }
        catch (Exception erro)
        { http?.Dispose(); return (null, null, null, null, null, null, "SD_SAP_" + erro.GetType().Name); }
    }
}

/// <summary>
/// Defesa em profundidade dos passos 3, 4 e 5. Daqui sai:
///
///   GET    no servico de ordens de venda (setor de atividade), na colecao de remessas (fetch do
///          CSRF), nas entidades de remessa (ETag e itens) e no texto da remessa;
///   POST   na COLECAO de cabecalhos de remessa (criar remessa) e na navegacao de texto de uma
///          remessa (criar o ZNFE que ainda nao existe);
///   PATCH  em ENTIDADES, com chave: o cabecalho de uma remessa, um item dela, ou o seu texto.
///
/// A distincao entre colecao e entidade e o que importa no PATCH: PATCH na colecao nao tem alvo
/// definido. Qualquer outro metodo, origem ou rota e recusado antes de virar requisicao -
/// inclusive um redirecionamento que apontasse para fora. Em particular, saida de mercadoria e
/// picking (passo 6) ainda NAO saem daqui.
/// </summary>
public sealed class RemessaSdHttpGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        var origemOk = uri is not null
            && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.GetLeftPart(UriPartial.Authority) == OrdensVendaSdComposition.OrigemAprovada;
        var caminho = origemOk ? uri!.AbsolutePath : "";

        // Colecao: a rota exata. Entidade: a rota seguida da chave entre parenteses.
        static bool Entidade(string caminho, string colecao)
            => caminho.StartsWith(colecao + "(", StringComparison.Ordinal)
               && caminho.EndsWith(")", StringComparison.Ordinal);

        var colecaoRemessa = caminho.Equals(RemessaSdSapClient.Rota, StringComparison.Ordinal);
        var entidadeRemessa = Entidade(caminho, AjusteRemessaSdSapClient.Cabecalho)
                              || Entidade(caminho, AjusteRemessaSdSapClient.Item);
        // Texto: a navegacao a partir de UMA remessa, e a entidade do texto com a sua chave.
        var navegacaoTexto = origemOk
            && caminho.StartsWith(AjusteRemessaSdSapClient.Cabecalho + "(", StringComparison.Ordinal)
            && caminho.EndsWith(TextoNfeSdSapClient.Navegacao, StringComparison.Ordinal);
        var entidadeTexto = Entidade(caminho, TextoNfeSdSapClient.Entidade);
        var ordem = origemOk
            && caminho.StartsWith(OrdensVendaSdComposition.RotaAprovada, StringComparison.Ordinal);
        // Passo 6: SO o picking. A saida de mercadoria e feita dentro do SAP e nao sai daqui -
        // ela move estoque e nao se desfaz por API neste fluxo.
        var expedicao = origemOk
            && caminho.Equals(ExpedicaoSdSapClient.RotaPicking, StringComparison.Ordinal);

        var liberado =
            request.Method == HttpMethod.Post ? colecaoRemessa || navegacaoTexto || expedicao
            : request.Method == HttpMethod.Patch ? entidadeRemessa || entidadeTexto
            : request.Method == HttpMethod.Get
                && (colecaoRemessa || entidadeRemessa || navegacaoTexto || ordem);
        if (!liberado) throw new InvalidOperationException("SD_REMESSA_ROTA_OU_METODO_PROIBIDO");
        return base.SendAsync(request, ct);
    }
}
