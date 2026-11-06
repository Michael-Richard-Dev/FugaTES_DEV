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

        // O par origem+client decide o ambiente deste processo. Conferido junto: host de um
        // ambiente com client do outro nao e ambiente nenhum.
        var ambiente = FugaTES.Aplicacao.Configuracao.AmbientesSap.Por(config["Sap:BaseUrl"], config["Sap:SapClient"])
            ?? throw new InvalidOperationException("SD_MOTOR_SAP_AMBIENTE_NAO_APROVADO");
        if (config["Guardian:BaseUrl"] != GuardianAprovado)
            throw new InvalidOperationException("SD_MOTOR_GUARDIAN_ORIGEM_NAO_APROVADA");

        var conexao = config["SdProtocolo:ConnectionString"]
            ?? throw new InvalidOperationException("SD_MOTOR_CONEXAO_AUSENTE");

        // O BANCO TEM DE SER O DO MESMO AMBIENTE DO SAP. Ate 06/11/2026 o motor conectava no que
        // estivesse configurado, sem conferir banco, papel, porta ou schema - e com um ambiente
        // so isso nunca apareceu, porque so havia um banco para onde ir.
        //
        // Com dois, e o cruzamento mais caro possivel: um motor apontado para o SAP QAS120 e para
        // o banco do DEV gravaria a remessa num ambiente e a pesagem no outro, as duas com
        // credencial valida e sem erro nenhum. Ninguem descobre ate alguem procurar uma remessa
        // que existe no lugar errado.
        //
        // Por isso nao se "aceitam os dois" aqui: deriva-se do ambiente SAP o UNICO banco que
        // serve. O par banco+papel continua sendo conferido junto - papel no PostgreSQL e do
        // cluster, nao do banco.
        ExigirBancoDoAmbiente(conexao, ambiente);

        services.AddSingleton(_ => new NpgsqlDataSourceBuilder(conexao).Build());
        services.AddSingleton<IRegistroProtocolosSd>(sp =>
            new RegistroProtocolosSdPostgreSql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IDevolutivasSdRepositorio>(sp =>
            new DevolutivasSdPostgreSql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IRemessasSdRepositorio>(sp =>
            new RemessasSdPostgreSql(sp.GetRequiredService<NpgsqlDataSource>()));

        // Um HttpClient por destino, cada um com a sua guarda: a do SAP so deixa passar GET na
        // colecao de ordens; a do Guardian so POST em token e protocolo.
        services.AddKeyedSingleton("sap", (_, _) =>
            new HttpClient(new MotorSdSapGuard(new HttpClientHandler { AllowAutoRedirect = false }, ambiente.Origem)));
        services.AddKeyedSingleton("guardian", (_, _) =>
            new HttpClient(new MotorSdGuardianGuard(new HttpClientHandler { AllowAutoRedirect = false })));

        // Leitor proprio, e nao o da Toledo: o systemd desta VM cria as credenciais como 0440 e
        // a classe compartilhada so abre excecao para o diretorio daquele servico. Ver
        // MotorSdCredenciais.
        services.AddSingleton(_ => new MotorSdCredenciais(
            Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY")
            ?? throw new InvalidOperationException("SD_MOTOR_CREDENCIAIS_SYSTEMD_AUSENTES")));
        services.AddSingleton<IAutenticacaoSap>(sp =>
            new MotorSdSapAuthentication(new Uri(ambiente.Origem),
                sp.GetRequiredService<MotorSdCredenciais>()));

        services.AddSingleton(sp => new SapODataHttpExecutor(
            sp.GetRequiredKeyedService<HttpClient>("sap"),
            new OpcoesSap
            {
                BaseUrl = new Uri(ambiente.Origem),
                SapClient = ambiente.SapClient,
                Timeout = TimeSpan.FromSeconds(60)
            },
            sp.GetRequiredService<IAutenticacaoSap>(),
            new CookieContainer()));
        services.AddSingleton<IConsultaOrdensVendaSdSap>(sp =>
            new OrdensVendaSdConsultaAdapter(sp.GetRequiredService<SapODataHttpExecutor>()));
        RegistrarClientesSdSap(services);
        RegistrarGuardianEMotoresSd(services);
    }

    /// <summary>
    /// Os clientes dos passos 3, 4 e 5. Separado para poder ser conferido em teste: a composicao
    /// inteira so roda em Linux, e por isso nunca era construida fora da VM - foi assim que a
    /// falta de IConsultarSetorAtividadeSd so apareceu ao subir o servico, em 02/11/2026.
    /// </summary>
    /// <summary>
    /// A cadeia de conexao tem de apontar para o banco DO MESMO AMBIENTE do SAP.
    ///
    /// Metodo proprio, e publico, por uma razao pratica: ConfigurarServicos recusa rodar fora do
    /// Linux antes de chegar aqui, entao a regra nao teria como ser exercitada em teste. Uma
    /// trava que nao se consegue testar e uma trava em que ninguem reparou quando parou de valer.
    ///
    /// O par banco+papel e conferido JUNTO - papel no PostgreSQL e do CLUSTER, nao do banco.
    /// </summary>
    public static void ExigirBancoDoAmbiente(string conexao, FugaTES.Aplicacao.Configuracao.AmbienteSap ambiente)
    {
        ArgumentNullException.ThrowIfNull(ambiente);
        var bancoDoAmbiente = FugaTES.Aplicacao.Configuracao.AmbientesBanco.PorNome(ambiente.Nome)
            ?? throw new InvalidOperationException("SD_MOTOR_BANCO_AMBIENTE_DESCONHECIDO");

        NpgsqlConnectionStringBuilder cadeia;
        try { cadeia = new NpgsqlConnectionStringBuilder(conexao); }
        catch { throw new InvalidOperationException("SD_MOTOR_BANCO_NAO_CONFERE_COM_O_AMBIENTE"); }

        // SEARCH PATH: ausente E VALIDO, e esta e uma correcao de uma exigencia que eu inventei.
        //
        // A primeira versao desta guarda, em 06/11/2026, exigia SearchPath="fugates". O motor do
        // DEV110 roda ha semanas SEM ele na cadeia - e teria parado de subir na publicacao
        // seguinte. O conferir-guarda-motor.sh pegou isso antes.
        //
        // E a exigencia era vazia de qualquer forma: TODAS as consultas dos repositorios SD
        // qualificam o schema ("fugates.protocolo_sd_ordem_venda"), entao o search_path nao e o
        // que decide onde se grava. Exigir um valor que nao protege nada so cria uma forma nova
        // de o servico nao subir.
        //
        // Declarado diferente de "fugates" ainda e recusado: quem escreve um schema na cadeia
        // esta afirmando algo, e afirmar o errado e diferente de nao afirmar.
        if (cadeia.Port != 5433
            || (cadeia.SearchPath is { Length: > 0 } sp && sp != "fugates")
            || (cadeia.Host != "127.0.0.1" && cadeia.Host != "localhost")
            || cadeia.Database != bancoDoAmbiente.Banco
            || cadeia.Username != bancoDoAmbiente.PapelSd)
            throw new InvalidOperationException("SD_MOTOR_BANCO_NAO_CONFERE_COM_O_AMBIENTE");
    }

    public static void RegistrarClientesSdSap(IServiceCollection services)
    {
        // Os quatro clientes dos passos 3, 4 e 5. Faltavam: a composicao tinha sido escrita so
        // para o motor de protocolo, e o servico nao subia por nao achar IConsultarSetorAtividadeSd.
        services.AddSingleton<IConsultarSetorAtividadeSd>(sp =>
            new SetorAtividadeSdConsulta(sp.GetRequiredService<SapODataHttpExecutor>()));
        // As linhas da ordem, para o biodiesel montar a remessa com as que ainda estao em aberto.
        services.AddSingleton<IConsultarLinhasOrdemSd>(sp =>
            new LinhasOrdemSdConsulta(sp.GetRequiredService<SapODataHttpExecutor>()));
        services.AddSingleton<ICriarRemessaSdSap>(sp =>
            new RemessaSdSapClient(sp.GetRequiredService<SapODataHttpExecutor>()));
        services.AddSingleton<IAjustarRemessaSdSap>(sp =>
            new AjusteRemessaSdSapClient(sp.GetRequiredService<SapODataHttpExecutor>()));
        services.AddSingleton<IGravarTextoNfeSd>(sp =>
            new TextoNfeSdSapClient(sp.GetRequiredService<SapODataHttpExecutor>()));
        // Expedicao: so o picking de casca e fibra sai daqui. A saida de mercadoria e do SAP.
        services.AddSingleton<IExpedirRemessaSdSap>(sp =>
            new ExpedicaoSdSapClient(sp.GetRequiredService<SapODataHttpExecutor>()));
    }

    /// <summary>O envio ao Guardian e os dois motores que rodam no mesmo processo.</summary>
    public static void RegistrarGuardianEMotoresSd(IServiceCollection services)
    {
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

        // O mesmo processo leva os dois motores: varrem o mesmo SAP, com as mesmas credenciais e
        // o mesmo banco. Separar em dois servicos duplicaria credencial e unidade sem ganho.
        services.AddSingleton(sp => new ExecutorFluxoRemessaSd(
            sp.GetRequiredService<IRemessasSdRepositorio>(),
            sp.GetRequiredService<IConsultarSetorAtividadeSd>(),
            sp.GetRequiredService<ICriarRemessaSdSap>(),
            sp.GetRequiredService<IAjustarRemessaSdSap>(),
            sp.GetRequiredService<IGravarTextoNfeSd>(),
            sp.GetRequiredService<IConsultarLinhasOrdemSd>()));
        services.AddHostedService<SdProtocoloWorker>();
        services.AddHostedService<RemessaSdWorker>();
    }
}

/// <summary>Do motor sai GET, e so na colecao de ordens de venda do SAP.</summary>
/// <summary>
/// A origem vem do processo, nao de uma constante: o motor de DEV110 e o de QAS120 rodam o MESMO
/// binario e so se distinguem pelo que tem configurado. Uma constante aqui deixaria os dois
/// guardando o mesmo endereco - e o de QAS recusaria as proprias chamadas.
/// </summary>
public sealed class MotorSdSapGuard(HttpMessageHandler inner, string origemAprovada) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        var origemOk = uri is not null
            && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.GetLeftPart(UriPartial.Authority) == origemAprovada;
        var caminho = origemOk ? uri!.AbsolutePath : "";

        // Colecao: a rota exata. Entidade: a rota seguida da chave entre parenteses. A distincao
        // importa no PATCH: PATCH na colecao nao tem alvo definido.
        static bool Entidade(string caminho, string colecao)
            => caminho.StartsWith(colecao + "(", StringComparison.Ordinal)
               && caminho.EndsWith(")", StringComparison.Ordinal);

        var ordem = origemOk
            && caminho.StartsWith(SdProtocoloComposition.RotaSapAprovada, StringComparison.Ordinal);
        var colecaoRemessa = caminho.Equals(RemessaSdSapClient.Rota, StringComparison.Ordinal);
        var entidadeRemessa = Entidade(caminho, AjusteRemessaSdSapClient.Cabecalho)
                              || Entidade(caminho, AjusteRemessaSdSapClient.Item);
        var navegacaoTexto = origemOk
            && caminho.StartsWith(AjusteRemessaSdSapClient.Cabecalho + "(", StringComparison.Ordinal)
            && caminho.EndsWith(TextoNfeSdSapClient.Navegacao, StringComparison.Ordinal);
        var entidadeTexto = Entidade(caminho, TextoNfeSdSapClient.Entidade);

        // Expedicao: separacao e saida de mercadoria, atras da MESMA chave do escopo. Desligada
        // em 04/11/2026, as duas rotas deixam de sair daqui - o motor nem chega a tenta-las,
        // porque consulta EscopoExpedicaoSd antes.
        var expedicao = EscopoExpedicaoSd.ExpedicaoHabilitada && origemOk
            && (caminho.Equals(ExpedicaoSdSapClient.RotaPicking, StringComparison.Ordinal)
                || caminho.Equals(ExpedicaoSdSapClient.RotaSaida, StringComparison.Ordinal));
        // O $batch: a rota do LOTE, exata. E POST por ser o verbo do envelope, nao da escrita -
        // o que vai DENTRO dele sao os mesmos PATCHes de item que a linha de baixo ja permite, e
        // nenhuma outra entidade e alcancavel por ele: o corpo e montado por nos, item a item.
        //
        // Sem esta linha a guarda recusa o ajuste inteiro com rota proibida. Ela fez exatamente o
        // que devia em 06/11/2026, quando o $batch entrou sem passar por aqui.
        var loteRemessa = caminho.Equals(
            AjusteRemessaSdSapClient.Servico + SapODataLoteV2.Sufixo, StringComparison.Ordinal);
        // Os mesmos limites da tela do operador - nem um a mais.
        var liberado =
            request.Method == HttpMethod.Post ? colecaoRemessa || navegacaoTexto || expedicao || loteRemessa
            : request.Method == HttpMethod.Patch ? entidadeRemessa || entidadeTexto
            : request.Method == HttpMethod.Get
                && (ordem || colecaoRemessa || entidadeRemessa || navegacaoTexto);
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
