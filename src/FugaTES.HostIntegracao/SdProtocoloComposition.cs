using System.Net;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Persistencia.Sd;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FugaTES.HostIntegracao;

/// <summary>
/// Monta o motor de protocolos SD para rodar como servico na VM (modo "SdProtocolo").
///
/// Na VM nao ha tela, nao ha Gerenciador de Credenciais do Windows e nao ha tunel SSH: o
/// PostgreSQL e local e os segredos vem por credencial do systemd, exatamente como ja fazem o
/// receptor da Toledo e o carrier. Esta classe nao le senha de arquivo de configuracao nem de
/// variavel de ambiente, e nada do que ela registra escreve segredo em log.
/// </summary>
public static class SdProtocoloComposition
{
    /// <summary>Origem SAP de DEV aprovada, a mesma das demais leituras.</summary>
    public const string OrigemSapAprovada = "https://vhfufds4ci.sap.fugacouros.com.br:44300";

    /// <summary>Rota liberada no SAP: so a colecao de ordens de venda.</summary>
    public const string RotaSapAprovada = "/sap/opu/odata/sap/API_SALES_ORDER_SRV/";

    public const string GuardianAprovado = "https://guardian.cloudprix.com.br";

    public static void ConfigurarServicos(IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // Integracao com o systemd: sem isto, uma unidade Type=notify ficaria esperando um aviso
        // de "pronto" que nunca chega, e o servico seria morto por timeout ao subir. Tambem e o
        // que faz o log sair no journal com a severidade certa.
        services.AddSystemd();
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<SdProtocoloOptions>(config.GetSection(SdProtocoloOptions.Secao));

        // O motor so existe em Linux porque so la as credenciais do systemd existem. Em outro
        // sistema ele subiria sem credencial e falharia a cada volta.
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("SD_MOTOR_SOMENTE_LINUX");

        if (config["Sap:BaseUrl"] != OrigemSapAprovada)
            throw new InvalidOperationException("SD_MOTOR_SAP_ORIGEM_NAO_APROVADA");
        if (string.IsNullOrWhiteSpace(config["Sap:SapClient"]))
            throw new InvalidOperationException("SD_MOTOR_SAP_CLIENT_AUSENTE");
        if (config["Guardian:BaseUrl"] != GuardianAprovado)
            throw new InvalidOperationException("SD_MOTOR_GUARDIAN_ORIGEM_NAO_APROVADA");

        var conexao = config["SdProtocolo:ConnectionString"]
            ?? throw new InvalidOperationException("SD_MOTOR_CONEXAO_AUSENTE");
        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(conexao).Build());
        services.AddSingleton<IRegistroProtocolosSd>(sp =>
            new RegistroProtocolosSdPostgreSql(sp.GetRequiredService<NpgsqlDataSource>()));

        // Um HttpClient por destino, cada um com a sua guarda: a do SAP so deixa passar GET na
        // colecao de ordens; a do Guardian so POST em token e protocolo.
        services.AddKeyedSingleton("sap", (_, _) =>
            new HttpClient(new MotorSdSapGuard(new HttpClientHandler { AllowAutoRedirect = false })));
        services.AddKeyedSingleton("guardian", (_, _) =>
            new HttpClient(new MotorSdGuardianGuard(new HttpClientHandler { AllowAutoRedirect = false })));

        // Leitor proprio, e nao o da Toledo: o systemd desta VM cria as credenciais como 0440 e
        // a classe compartilhada so abre excecao para o diretorio daquele servico. Ver
        // MotorSdCredenciais.
        services.AddSingleton(_ => new MotorSdCredenciais(
            Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY")
            ?? throw new InvalidOperationException("SD_MOTOR_CREDENCIAIS_SYSTEMD_AUSENTES")));
        services.AddSingleton<IAutenticacaoSap>(sp =>
            new MotorSdSapAuthentication(new Uri(OrigemSapAprovada),
                sp.GetRequiredService<MotorSdCredenciais>()));

        services.AddSingleton(sp => new SapODataHttpExecutor(
            sp.GetRequiredKeyedService<HttpClient>("sap"),
            new OpcoesSap
            {
                BaseUrl = new Uri(OrigemSapAprovada),
                SapClient = config["Sap:SapClient"],
                Timeout = TimeSpan.FromSeconds(60)
            },
            sp.GetRequiredService<IAutenticacaoSap>(),
            new CookieContainer()));
        services.AddSingleton<IConsultaOrdensVendaSdSap>(sp =>
            new OrdensVendaSdConsultaAdapter(sp.GetRequiredService<SapODataHttpExecutor>()));

        services.AddSingleton(_ => new GuardianHttpOptions
        {
            BaseUrl = new Uri(GuardianAprovado),
            Timeout = TimeSpan.FromSeconds(30),
            PreCadastroEndpoint = GuardianProtocoloSdClient.Rota
        });
        services.AddSingleton<ICredenciaisGuardianSet26Provider>(sp =>
            new MotorSdGuardianCredenciais(sp.GetRequiredService<MotorSdCredenciais>()));
        services.AddSingleton<IEnviarProtocoloSdGateway>(sp =>
        {
            var http = sp.GetRequiredKeyedService<HttpClient>("guardian");
            var opcoes = sp.GetRequiredService<GuardianHttpOptions>();
            return new GuardianProtocoloSdClient(http, opcoes,
                new GuardianTokenClient(http, opcoes,
                    sp.GetRequiredService<ICredenciaisGuardianSet26Provider>()));
        });

        services.AddHostedService<SdProtocoloWorker>();
    }
}

/// <summary>Do motor sai GET, e so na colecao de ordens de venda do SAP.</summary>
public sealed class MotorSdSapGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        var liberado = uri is not null
            && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.GetLeftPart(UriPartial.Authority) == SdProtocoloComposition.OrigemSapAprovada
            && request.Method == HttpMethod.Get
            && uri.AbsolutePath.StartsWith(SdProtocoloComposition.RotaSapAprovada, StringComparison.Ordinal);
        if (!liberado) throw new InvalidOperationException("SD_MOTOR_SAP_ROTA_OU_METODO_PROIBIDO");
        return base.SendAsync(request, ct);
    }
}

/// <summary>
/// Do motor sai POST para o Guardian, e so em token e protocolo. Nenhuma rota de emissor, item,
/// contrato ou transportadora e alcancavel daqui.
/// </summary>
public sealed class MotorSdGuardianGuard(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const string Token = "/api/v2/CreateToken";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        var liberado = uri is not null
            && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.GetLeftPart(UriPartial.Authority) == SdProtocoloComposition.GuardianAprovado
            && request.Method == HttpMethod.Post
            && (uri.AbsolutePath == GuardianProtocoloSdClient.Rota || uri.AbsolutePath == Token);
        if (!liberado) throw new InvalidOperationException("SD_MOTOR_GUARDIAN_ROTA_OU_METODO_PROIBIDO");
        return base.SendAsync(request, ct);
    }
}
