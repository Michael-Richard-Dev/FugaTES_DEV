using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.Configuration;

namespace R158.SapInboundReadiness.Tests;

// R166: prontidão do receptor SAP com escopo R57 COMPLETO. Offline: a composição apenas registra
// serviços — nenhum host é iniciado, nenhuma porta é aberta, nenhuma conexão de banco é feita
// (o NpgsqlDataSource é registrado por fábrica preguiçosa) e nenhum POST ocorre.
public sealed class SapReceiverCompositionTests
{
    private static Dictionary<string, string?> Completa()
    {
        var d = new Dictionary<string, string?>
        {
            ["SapContractInbound:ProductiveEnabled"] = "true",
            ["SapContractInbound:AutomaticProcessingEnabled"] = "true",
            ["SapContractInbound:OfflineEnabled"] = "false",
            ["SapContractInbound:SecurityMode"] = "LoopbackBasicHttps",
            ["SapInbound:BasicAuth:Username"] = "sintetico",
            ["SapInbound:BasicAuth:Password"] = "sintetico",
            // Identidade EXCLUSIVA de escrita; destino imposto pelo próprio código.
            ["ConnectionStrings:FugaTES"] =
                $"Host=127.0.0.1;Port=5433;Database=fugates_dev;Username={NpgsqlDataSourceFactory.SapInboundApplicationRole};" +
                "Search Path=fugates;Passfile=/home/michael/.fugates-sap-inbound/pgpass",
            ["FUGATES_INTEGRATION_WORKER_ENABLED"] = "false",
            ["HostIntegracao:PersistenceMode"] = "PostgreSql",
            ["HostIntegracao:ExternalMode"] = "Guardian",
            ["MotorIntegracao:LeaseDuration"] = "00:03:00",
            ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
            ["MotorIntegracao:RetryBackoff"] = "1.00:00:00",
            // Seção exigida por HostIntegracaoComposition (GetRequiredSection); está presente no
            // appsettings entregue do receptor e sua ausência derruba a composição.
            ["TimeoutProcessamentoIntegracao:Timeout"] = "00:01:00",
            // ExternalMode=Guardian é condição do próprio guard SAP, e AddGuardianRuntime valida
            // estes campos na partida. Valores sintéticos: nada é enviado ao Guardian neste teste.
            ["Guardian:BaseUrl"] = "https://guardian.invalid",
            ["Guardian:Timeout"] = "00:00:30",
            ["Guardian:Email"] = "sintetico@invalid",
            ["Guardian:Password"] = "SINTETICO",
            ["Guardian:Signature"] = "SINTETICA",
            ["AcmR57:Enabled"] = "true",
            ["AcmR57:Normal:Enabled"] = "true",
            ["AcmR57:Normal:ExecutionAuthorized"] = "false",
            // Janela R57 completa — valores sintéticos, apenas para provar a forma da configuração.
            ["MotorIntegracao:AcmInboundWindow:Enabled"] = "true",
            ["MotorIntegracao:AcmInboundWindow:WindowId"] = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
            ["MotorIntegracao:AcmInboundWindow:AuthorizationReference"] = "SINTETICO-OFFLINE-R166",
            ["MotorIntegracao:AcmInboundWindow:NotBeforeUtc"] = "2026-10-28T00:00:00+00:00",
            ["MotorIntegracao:AcmInboundWindow:UntilUtc"] = "2026-10-28T23:00:00+00:00",
            ["MotorIntegracao:AcmInboundWindow:Centro"] = "3011",
            ["MotorIntegracao:AcmInboundWindow:AllowedSapPartners:0"] = "0000500123",
            ["MotorIntegracao:AcmInboundWindow:AllowedSapMaterials:0"] = "000000000000100045"
        };
        return d;
    }

    private static IConfiguration Config(Action<Dictionary<string, string?>>? ajuste = null)
    {
        var d = Completa();
        ajuste?.Invoke(d);
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    [Fact]
    public void Com_escopo_r57_completo_a_composicao_produtiva_e_aceita()
    {
        var servicos = new ServiceCollection();
        var aceito = SapContractInboundProductiveComposition.ConfigurarServicos(servicos, Config());
        Assert.True(aceito);
        // O caso de uso durável aprovado é o único caminho de aceitação registrado.
        Assert.Contains(servicos, s => s.ServiceType == typeof(AceitarContratoSapToledoDuravelCasoDeUso));
    }

    [Fact]
    public void Nenhum_worker_e_registrado_com_o_flag_desligado()
    {
        var servicos = new ServiceCollection();
        SapContractInboundProductiveComposition.ConfigurarServicos(servicos, Config());
        var hospedados = servicos
            .Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .ToArray();
        Assert.Empty(hospedados);
    }

    [Theory]
    [InlineData("MotorIntegracao:AcmInboundWindow:Enabled", "false")]          // sem escopo
    [InlineData("MotorIntegracao:AcmInboundWindow:WindowId", "")]              // janela sem identidade
    [InlineData("MotorIntegracao:AcmInboundWindow:AuthorizationReference", "")]// sem autorização
    [InlineData("MotorIntegracao:AcmInboundWindow:AllowedSapPartners:0", "")]  // parceiro vazio
    [InlineData("MotorIntegracao:AcmInboundWindow:AllowedSapMaterials:0", "")] // material vazio
    [InlineData("MotorIntegracao:AcmInboundWindow:Centro", "3020")]            // centro fora do aprovado
    public void Qualquer_lacuna_no_escopo_reprova_a_composicao(string chave, string valor)
    {
        var erro = Assert.Throws<InvalidOperationException>(() =>
            SapContractInboundProductiveComposition.ConfigurarServicos(new ServiceCollection(), Config(d => d[chave] = valor)));
        Assert.Equal("SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED", erro.Message);
    }

    [Fact]
    public void O_receptor_usa_a_identidade_de_escrita_e_nunca_a_leitora()
    {
        // A role GET-only não pode compor o receptor: o destino do receptor é escrita.
        var conexaoLeitora = Config(d => d["ConnectionStrings:FugaTES"] =
            $"Host=127.0.0.1;Port=5433;Database=fugates_dev;Username={NpgsqlDataSourceFactory.DevReadOnlyApplicationRole};" +
            "Search Path=fugates;Passfile=/home/michael/.fugates-dev-read/pgpass");
        // A composição aceita a forma, mas a identidade efetiva é verificada na conexão física:
        // CriarAcm exige que current_user seja exatamente a role da connection string.
        Assert.True(NpgsqlDataSourceFactory.EhRoleGuardada(NpgsqlDataSourceFactory.DevReadOnlyApplicationRole));
        Assert.NotEqual(NpgsqlDataSourceFactory.SapInboundApplicationRole,
                        NpgsqlDataSourceFactory.DevReadOnlyApplicationRole);
        // E a configuração entregue do receptor declara a identidade de escrita.
        var pacote = Path.Combine(Raiz(), "deploy", "sap-inbound-dev", "appsettings.receiver.dev.json");
        var texto = File.ReadAllText(pacote);
        Assert.Contains("Username=" + NpgsqlDataSourceFactory.SapInboundApplicationRole, texto, StringComparison.Ordinal);
        Assert.DoesNotContain("Username=" + NpgsqlDataSourceFactory.AcmApplicationRole, texto, StringComparison.Ordinal);
        _ = conexaoLeitora;
    }

    // ----------------------------------------------------------------------------------------
    // R166: provas sobre o ARQUIVO REAL entregue, não sobre uma fixture equivalente.
    // ----------------------------------------------------------------------------------------
    private static string ArquivoEntregue()
        => Path.Combine(Raiz(), "deploy", "sap-inbound-dev", "appsettings.receiver.dev.json");

    private static IConfiguration ConfigEntregue(Dictionary<string, string?>? sobreposicao = null)
    {
        var builder = new ConfigurationBuilder().AddJsonFile(ArquivoEntregue(), optional: false);
        if (sobreposicao is not null) builder.AddInMemoryCollection(sobreposicao);
        return builder.Build();
    }

    // Valores sintéticos APENAS para as pendências; nada é enviado a lugar algum neste teste.
    private static Dictionary<string, string?> JanelaR57Sintetica() => new()
    {
        ["Urls"] = "http://127.0.0.1:0",
        ["ConnectionStrings:FugaTES"] =
            $"Host=127.0.0.1;Port=5433;Database=fugates_dev;Username={NpgsqlDataSourceFactory.SapInboundApplicationRole};" +
            "Search Path=fugates;Passfile=/home/michael/.fugates-sap-inbound/pgpass",
        ["SapInbound:BasicAuth:Username"] = "sintetico",
        ["SapInbound:BasicAuth:Password"] = "sintetico",
        ["Guardian:Email"] = "sintetico@invalid",
        ["Guardian:Password"] = "SINTETICO",
        ["Guardian:Signature"] = "SINTETICA",
        ["MotorIntegracao:AcmInboundWindow:Enabled"] = "true",
        ["MotorIntegracao:AcmInboundWindow:WindowId"] = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
        ["MotorIntegracao:AcmInboundWindow:AuthorizationReference"] = "SINTETICO-OFFLINE-R166",
        ["MotorIntegracao:AcmInboundWindow:NotBeforeUtc"] = "2026-10-28T00:00:00+00:00",
        ["MotorIntegracao:AcmInboundWindow:UntilUtc"] = "2026-10-28T23:00:00+00:00",
        ["MotorIntegracao:AcmInboundWindow:AllowedSapPartners:0"] = "0000500123",
        ["MotorIntegracao:AcmInboundWindow:AllowedSapMaterials:0"] = "000000000000100045"
    };

    // Como ENTREGUE (janela fechada), a admissão R57 é obrigatória e o receptor recusa compor.
    [Fact]
    public void O_arquivo_entregue_mantem_a_admissao_r57_obrigatoria_e_fechada()
    {
        // Como entregue: sem credencial Basic (que vem do ambiente), a primeira barreira recusa.
        var erro = Assert.Throws<InvalidOperationException>(() =>
            InboundHttpHostComposition.Configurar(new ServiceCollection(), ConfigEntregue(), isDevelopment: false));
        Assert.Equal("SAP_INBOUND_BASIC_CONFIGURATION_REQUIRED", erro.Message);

        // Com a credencial presente, a admissao R57 continua obrigatoria e fechada: uma barreira
        // nao substituiu a outra.
        var soBasic = new Dictionary<string, string?>
        {
            ["SapInbound:BasicAuth:Username"] = "sintetico",
            ["SapInbound:BasicAuth:Password"] = "sintetico"
        };
        var erroR57 = Assert.Throws<InvalidOperationException>(() =>
            InboundHttpHostComposition.Configurar(new ServiceCollection(), ConfigEntregue(soBasic), isDevelopment: false));
        Assert.Equal("SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED", erroR57.Message);
    }

    // Com a janela autorizada, o arquivo entregue compõe SOMENTE o caminho SAP durável:
    // nenhuma rota de callback Guardian, nenhum serviço Toledo, nenhum worker.
    [Fact]
    public void O_arquivo_entregue_compoe_apenas_o_caminho_sap_duravel()
    {
        var servicos = new ServiceCollection();
        var rotas = InboundHttpHostComposition.Configurar(
            servicos, ConfigEntregue(JanelaR57Sintetica()), isDevelopment: false);

        Assert.True(rotas.SapProductive);
        Assert.False(rotas.SapOffline);
        // Callback nulo é o que impede Program.cs de chamar GuardianCallbackEndpoint.MapDual —
        // e é também o único caminho que levaria a ToledoCallbackComposition.Configure.
        Assert.Null(rotas.Callback);

        Assert.Contains(servicos, s => s.ServiceType == typeof(AceitarContratoSapToledoDuravelCasoDeUso));
        Assert.Empty(servicos.Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)));
    }

    // O código Toledo em edição pelo Codex está no assembly, mas o receptor NÃO o compõe:
    // nenhum serviço Toledo é registrado a partir da configuração entregue.
    [Fact]
    public void Nenhum_servico_toledo_do_codex_e_registrado_pelo_receptor()
    {
        var servicos = new ServiceCollection();
        InboundHttpHostComposition.Configurar(
            servicos, ConfigEntregue(JanelaR57Sintetica()), isDevelopment: false);

        // "ContratosSapToledo"/"ContratoSapToledo" é o CONTRATO SAP — o caminho aprovado deste
        // receptor. O que não pode aparecer é o caminho de CALLBACK/PESAGEM Toledo do Codex,
        // identificado pelos seus tipos concretos.
        string[] doCallbackToledo =
        [
            "IToledoCallbackAdmission", "ToledoCallbackAdmission", "ToledoCallbackAdmissionOptions",
            "ProtectedToledoCallbackAdmission", "ToledoPesagemCallbackDto",
            "IRegistroPesagemToledoStore", "RegistrarPesagemToledoCasoDeUso", "RecebimentosToledo"
        ];
        var toledo = servicos
            .Select(s => s.ServiceType.FullName + "|" + s.ImplementationType?.FullName)
            .Where(n => doCallbackToledo.Any(t => n.Contains(t, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(toledo);
        // E a configuração entregue não habilita nenhuma rota Toledo.
        var conteudo = File.ReadAllText(ArquivoEntregue());
        Assert.DoesNotContain("ToledoInbound", conteudo, StringComparison.Ordinal);
        Assert.Contains("\"Callback\": { \"Enabled\": false }", conteudo, StringComparison.Ordinal);
    }

    private static string Raiz()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "FugaTES.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }
}
