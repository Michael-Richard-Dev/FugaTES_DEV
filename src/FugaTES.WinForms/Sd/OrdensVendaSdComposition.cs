using System.Net;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.WinForms.Runtime;
using FugaTES.WinForms.Transportadoras;
using Microsoft.Extensions.Configuration;

namespace FugaTES.WinForms.Sd;

/// <summary>
/// SD: montagem da tela de ordens de venda. Leitura apenas.
///
/// Reusa o perfil ja implantado (FugaTES.carrier.json) porque e ele que carrega a origem SAP
/// aprovada e a REFERENCIA da credencial - nunca a senha, que fica no Gerenciador de Credenciais
/// do Windows. Quando o perfil nao fecha, a tela abre bloqueada com o motivo em vez de nao abrir:
/// o objetivo aqui e justamente descobrir o que falta para a busca funcionar.
/// </summary>
public static class OrdensVendaSdComposition
{
    /// <summary>Origem SAP de DEV aprovada, a mesma das demais leituras do desktop.</summary>
    public const string OrigemAprovada = "https://vhfufds4ci.sap.fugacouros.com.br:44300";

    /// <summary>Unica rota liberada: a colecao de ordens de venda do servico da planilha.</summary>
    public const string RotaAprovada = "/sap/opu/odata/sap/API_SALES_ORDER_SRV/";

    public const string SapClientAprovado = "110";

    public static FrmOrdensVendaSd Criar()
    {
        HttpClient? http = null;
        try
        {
            var config = CarrierDesktopProfile.Load(Path.Combine(AppContext.BaseDirectory, "FugaTES.carrier.json"));
            Validar(config);
            http = new HttpClient(new OrdensVendaSdHttpGuard(new HttpClientHandler { AllowAutoRedirect = false }));
            var executor = new SapODataHttpExecutor(
                http,
                new OpcoesSap
                {
                    // Origem pura: o path vem da colecao, nao do perfil, que aponta para o servico
                    // de parceiros de negocio.
                    BaseUrl = new Uri(OrigemAprovada),
                    SapClient = SapClientAprovado,
                    Timeout = TimeSpan.FromSeconds(30)
                },
                new CarrierServiceSapAuthentication(config),
                new CookieContainer());
            return new FrmOrdensVendaSd(new OrdensVendaSdConsultaAdapter(executor), http);
        }
        catch (DesktopStartupException ex) { http?.Dispose(); return Bloqueada(ex.Code); }
        catch { http?.Dispose(); return Bloqueada("SD_SAP_PERFIL_INDISPONIVEL"); }
    }

    /// <summary>
    /// Confere origem, client e a referencia da credencial. Nao resolve a senha: isso acontece por
    /// requisicao, no Gerenciador de Credenciais, e nunca passa por aqui nem por arquivo.
    /// </summary>
    public static void Validar(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Uri.TryCreate(config["Sap:BaseUrl"], UriKind.Absolute, out var sap) || sap.Scheme != "https"
            || sap.UserInfo.Length != 0 || sap.GetLeftPart(UriPartial.Authority) != OrigemAprovada)
            throw new DesktopStartupException("SD_SAP_ORIGEM_NAO_APROVADA");
        if (config["Sap:SapClient"] != SapClientAprovado)
            throw new DesktopStartupException("SD_SAP_CLIENT_NAO_APROVADO");
        foreach (var chave in new[] { "SapUser", "SapCredentialTarget" })
            if (string.IsNullOrWhiteSpace(config["Carrier:Secrets:" + chave]))
                throw new DesktopStartupException("SD_SAP_CREDENCIAL_NAO_REFERENCIADA");
    }

    private static FrmOrdensVendaSd Bloqueada(string codigo) => new(
        new SemConsulta(),
        bloqueio: "Busca no SAP indisponível: " + codigo
            + ".\r\nA tela abriu somente para conferência; nada foi consultado, gravado ou enviado."
            + "\r\nEsperado em " + OrigemAprovada + RotaAprovada.TrimStart('/')
            + " com sap-client " + SapClientAprovado + " e a credencial do perfil do desktop.");

    // Nunca chamada com a tela bloqueada; existe para que a tela nao precise de um gateway nulo.
    private sealed class SemConsulta : Aplicacao.Contratos.Gateways.SAP.IConsultaOrdensVendaSdSap
    {
        public Task<Aplicacao.Resultados.SAP.ResultadoConsultaSap<IReadOnlyList<Aplicacao.Modelos.Sd.OrdemVendaSdLeitura>>>
            ConsultarAsync(Aplicacao.Modelos.Sd.OrdensVendaSdFiltro filtro, CancellationToken cancellationToken)
            => Task.FromResult(Aplicacao.Resultados.SAP.ResultadoConsultaSap<
                IReadOnlyList<Aplicacao.Modelos.Sd.OrdemVendaSdLeitura>>.Falha(
                    Aplicacao.Resultados.SAP.StatusConsultaSap.FalhaConfiguracao));
    }
}

/// <summary>
/// Defesa em profundidade: desta tela sai GET, e so para a colecao de ordens de venda. Qualquer
/// outro metodo, origem ou rota e recusado antes de virar requisicao - inclusive um redirecionamento
/// que apontasse para fora.
/// </summary>
public sealed class OrdensVendaSdHttpGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        var liberado = uri is not null
            && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.GetLeftPart(UriPartial.Authority) == OrdensVendaSdComposition.OrigemAprovada
            && request.Method == HttpMethod.Get
            && uri.AbsolutePath.StartsWith(OrdensVendaSdComposition.RotaAprovada, StringComparison.Ordinal);
        if (!liberado) throw new InvalidOperationException("SD_HTTP_ROTA_OU_METODO_PROIBIDO");
        return base.SendAsync(request, ct);
    }
}
