using FugaTES.Aplicacao.Configuracao;
using System.Net;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Persistencia.Sd;
using FugaTES.Aplicacao.Servicos.Sd;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.WinForms.Runtime;
using FugaTES.WinForms.Transportadoras;
using Microsoft.Extensions.Configuration;

using FugaTES.Aplicacao.Configuracao;
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

    /// <summary>
    /// O ambiente DESTE desktop, pelo par que ele tem configurado. Uma pasta, um ambiente: a
    /// mesma publicacao vai para C:/FugaTES_DEV110 e C:/FugaTES_QAS120, e o que as distingue e o
    /// FugaTES.carrier.json ao lado do executavel.
    ///
    /// Nao ha seletor de ambiente dentro do programa, de proposito. Ele faria um processo so
    /// segurar os dois, e as credenciais dos dois ambientes sao as MESMAS - um clique errado
    /// gravaria no SAP errado sem esbarrar em senha nenhuma.
    /// </summary>
    public static AmbienteSap Ambiente(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // O perfil do desktop guarda em Sap:BaseUrl a URL do servico de parceiros, COM caminho.
        // O ambiente e a autoridade dela - era isso que o guarda antigo comparava, e comparar a
        // URL inteira reprovaria um perfil valido.
        if (!Uri.TryCreate(config["Sap:BaseUrl"], UriKind.Absolute, out var url))
            throw new DesktopStartupException("SD_SAP_AMBIENTE_NAO_APROVADO");
        return AmbientesSap.Por(url, config["Sap:SapClient"])
            ?? throw new DesktopStartupException("SD_SAP_AMBIENTE_NAO_APROVADO");
    }

    /// <summary>
    /// Codigos de emissor e item que o operador ja cadastrou no Guardian, em arquivo ao lado do
    /// executavel para nao congelar um retrato do banco do Guardian dentro do binario. Ausente ou
    /// vazio significa "nao sei": a conferencia nao acontece e quem decide e o Guardian, no POST.
    /// </summary>
    public const string ArquivoCadastro = "FugaTES.sd-guardian.json";

    public static CadastroGuardianConhecido CarregarCadastro(string? conteudoJson)
    {
        if (string.IsNullOrWhiteSpace(conteudoJson)) return CadastroGuardianConhecido.Desconhecido;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(conteudoJson);
            static string[] Lista(System.Text.Json.JsonElement raiz, string nome)
                => raiz.TryGetProperty(nome, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? [.. v.EnumerateArray()
                        .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                        .Select(e => e.GetString()!.Trim())
                        .Where(s => s.Length > 0)]
                    : [];
            return new(Lista(doc.RootElement, "emissores"), Lista(doc.RootElement, "itens"));
        }
        // Arquivo corrompido nao pode liberar o envio por engano, nem impedir a tela de abrir.
        catch (System.Text.Json.JsonException) { return CadastroGuardianConhecido.Desconhecido; }
    }

    private static CadastroGuardianConhecido LerCadastro()
    {
        var caminho = Path.Combine(AppContext.BaseDirectory, ArquivoCadastro);
        try { return File.Exists(caminho) ? CarregarCadastro(File.ReadAllText(caminho)) : CadastroGuardianConhecido.Desconhecido; }
        catch (IOException) { return CadastroGuardianConhecido.Desconhecido; }
        catch (UnauthorizedAccessException) { return CadastroGuardianConhecido.Desconhecido; }
    }

    /// <summary>Origem do Guardian na nuvem, a mesma ja usada pelo pre-cadastro do ACM.</summary>
    public const string GuardianAprovado = "https://guardian.cloudprix.com.br";

    private sealed class Descartavel(params IDisposable?[] itens) : IDisposable
    {
        public void Dispose() { foreach (var i in itens) i?.Dispose(); }
    }

    /// <summary>
    /// Monta o remetente do protocolo. Devolve nulo quando o perfil do Guardian nao fecha: a tela
    /// continua lendo o SAP e mostrando a previsualizacao, apenas sem o botao de enviar.
    /// </summary>
    private static (IEnviarProtocoloSdGateway?, HttpClient?) CriarGuardian(IConfiguration config)
    {
        HttpClient? http = null;
        try
        {
            if (!Uri.TryCreate(config["Guardian:BaseUrl"], UriKind.Absolute, out var guardian)
                || guardian.Scheme != "https" || guardian.UserInfo.Length != 0
                || guardian.GetLeftPart(UriPartial.Authority) != GuardianAprovado)
                return (null, null);
            foreach (var chave in new[] { "GuardianUser", "GuardianCredentialTarget", "GuardianSignatureTarget", "GuardianSignatureUser" })
                if (string.IsNullOrWhiteSpace(config["Carrier:Secrets:" + chave])) return (null, null);

            http = new HttpClient(new ProtocoloSdHttpGuard(new HttpClientHandler { AllowAutoRedirect = false }));
            var opcoes = new GuardianHttpOptions
            {
                BaseUrl = new Uri(GuardianAprovado),
                Timeout = TimeSpan.FromSeconds(30),
                PreCadastroEndpoint = GuardianProtocoloSdClient.Rota
            };
            var credenciais = new CarrierServiceGuardianCredentials(config);
            return (new GuardianProtocoloSdClient(http, opcoes, new GuardianTokenClient(http, opcoes, credenciais)), http);
        }
        catch { http?.Dispose(); return (null, null); }
    }

    /// <summary>
    /// Registro duravel no PostgreSQL (tabela da migracao 007S1). Devolve nulo quando o banco nao
    /// esta disponivel - e a tela entao NAO envia, porque sem conferir a duplicidade o envio seria
    /// uma aposta. Antes isto era um arquivo ao lado do executavel, que dava um registro por
    /// instalacao: a que nao enviou nao sabia do protocolo criado pela outra.
    /// </summary>
    /// <summary>
    /// Abre a conexao sob demanda, e nao na montagem da tela. Montar e sincrono: esperar aqui o
    /// tunel SSH bloqueava a thread da interface - e travava de vez, porque a continuacao tentava
    /// voltar justamente para a thread bloqueada.
    /// </summary>
    private static async Task<(IRegistroProtocolosSd?, IDisposable?, string?)> AbrirRegistroAsync(CancellationToken ct)
    {
        try
        {
            var aberta = await ConexaoSdPostgreSql.AbrirAsync(ct).ConfigureAwait(false);
            return (aberta.Fonte is null ? null : new RegistroProtocolosSdPostgreSql(aberta.Fonte),
                    aberta.Posse, aberta.Motivo);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception erro) { return (null, null, "SD_BANCO_" + erro.GetType().Name); }
    }

    public static FrmOrdensVendaSd Criar()
    {
        HttpClient? http = null;
        try
        {
            var config = CarrierDesktopProfile.Load(Path.Combine(AppContext.BaseDirectory, "FugaTES.carrier.json"));
            Validar(config);
            var ambiente = Ambiente(config);
            http = new HttpClient(new OrdensVendaSdHttpGuard(new HttpClientHandler { AllowAutoRedirect = false }, ambiente.Origem));
            var executor = new SapODataHttpExecutor(
                http,
                new OpcoesSap
                {
                    // Origem pura: o path vem da colecao, nao do perfil, que aponta para o servico
                    // de parceiros de negocio.
                    BaseUrl = new Uri(ambiente.Origem),
                    SapClient = ambiente.SapClient,
                    Timeout = TimeSpan.FromSeconds(30)
                },
                new CarrierServiceSapAuthentication(config),
                new CookieContainer());
            // O envio ao Guardian e opcional: sem perfil de Guardian a tela abre so com a leitura
            // e a previsualizacao, em vez de nao abrir.
            var (guardian, httpGuardian) = CriarGuardian(config);
            return new FrmOrdensVendaSd(new OrdensVendaSdConsultaAdapter(executor),
                new Descartavel(http, httpGuardian), cadastro: LerCadastro(),
                guardian: guardian, abrirRegistro: AbrirRegistroAsync);
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
            || sap.UserInfo.Length != 0)
            throw new DesktopStartupException("SD_SAP_ORIGEM_NAO_APROVADA");
        _ = Ambiente(config);
        foreach (var chave in new[] { "SapUser", "SapCredentialTarget" })
            if (string.IsNullOrWhiteSpace(config["Carrier:Secrets:" + chave]))
                throw new DesktopStartupException("SD_SAP_CREDENCIAL_NAO_REFERENCIADA");
    }

    private static FrmOrdensVendaSd Bloqueada(string codigo) => new(
        new SemConsulta(),
        bloqueio: "Busca no SAP indisponível: " + codigo
            + ".\r\nA tela abriu somente para conferência; nada foi consultado, gravado ou enviado."
            // Os ambientes APROVADOS, nao um deles escrito: num desktop de QAS esta mensagem
            // dizia "esperado em DEV110" e mandava a pessoa conferir o lugar errado.
            + "\r\nEsperado em " + string.Join(" ou ", AmbientesSap.Aprovados
                .Select(a => a.Origem + RotaAprovada + " com sap-client " + a.SapClient))
            + ", com a credencial do perfil do desktop.");

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
/// <summary>A origem vem do processo: o mesmo binario serve DEV110 e QAS120.</summary>
public sealed class OrdensVendaSdHttpGuard(HttpMessageHandler inner, string origemAprovada) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        var liberado = uri is not null
            && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.GetLeftPart(UriPartial.Authority) == origemAprovada
            && request.Method == HttpMethod.Get
            && uri.AbsolutePath.StartsWith(OrdensVendaSdComposition.RotaAprovada, StringComparison.Ordinal);
        if (!liberado) throw new InvalidOperationException("SD_HTTP_ROTA_OU_METODO_PROIBIDO");
        return base.SendAsync(request, ct);
    }
}

/// <summary>
/// Defesa em profundidade do lado do Guardian: desta tela sai POST, e so para a autenticacao e
/// para a criacao do protocolo. Nenhuma outra rota do Guardian e alcancavel daqui - em especial
/// nenhuma de contrato, emissor, item ou transportadora.
/// </summary>
public sealed class ProtocoloSdHttpGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const string Token = "/api/v2/CreateToken";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        var liberado = uri is not null
            && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.GetLeftPart(UriPartial.Authority) == OrdensVendaSdComposition.GuardianAprovado
            && request.Method == HttpMethod.Post
            && (uri.AbsolutePath == GuardianProtocoloSdClient.Rota || uri.AbsolutePath == Token);
        if (!liberado) throw new InvalidOperationException("SD_GUARDIAN_ROTA_OU_METODO_PROIBIDO");
        return base.SendAsync(request, ct);
    }
}
