using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Integracao.Toledo.Set26;
using FugaTES.Integracao.SAP.Http;
using FugaTES.HostIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FugaTES.Testes;

public sealed class Inc006H6CallbackSapEngineTests
{
    [Fact]
    public async Task Callback_atomico_idempotente_e_worker_processa_sem_dto()
    {
        var (provider, correlations, motor, sap) = await FixtureAsync();
        using (provider)
        {
            var callback = new ProcessarCallbackGuardianMvpCasoDeUso(correlations,
                new RegistroCallbackPesagemSapMemoria(correlations, motor));
            var accepted = await callback.ExecutarAsync(Callback(), CancellationToken.None);
            var duplicate = await callback.ExecutarAsync(Callback(), CancellationToken.None);
            Assert.Equal(StatusCallbackGuardianMvp.Aceito, accepted.Status);
            Assert.Equal(StatusCallbackGuardianMvp.Idempotente, duplicate.Status);
            Assert.Null(accepted.Pesagem);
            Assert.Empty(sap.Recebidas);
            Assert.Single(motor.Integracoes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => callback.ExecutarAsync(
                Callback() with { PesoBruto = 1300m }, CancellationToken.None));
            Assert.Single(motor.Integracoes);

            // The worker has no callback DTO; it reconstructs its request from repository state.
            var worker = provider.GetRequiredService<MotorIntegracaoServico>();
            var result = await worker.ExecutarUmCicloAsync(CancellationToken.None);
            Assert.Equal(CicloMotorIntegracaoResultado.Processado, result);
            var sent = Assert.Single(sap.Recebidas);
            Assert.Equal("TICKET-H6", sent.IdPesagem);
            Assert.Equal(1200m, sent.PesoBruto);
            Assert.Equal(200m, sent.PesoTara);
            Assert.Equal(1000m, sent.PesoAntesDesconto);
            Assert.Equal("NF", sent.TipoDocumento);
            Assert.Equal("PO-H6", sent.PedidoCompras);
            Assert.Equal("ACM-H6", sent.ContratoACM);
            Assert.Equal("PROTO-H6", sent.NumeroProtocolo);
            Assert.Equal(EstadoIntegracao.Concluida, Assert.Single(motor.Integracoes).Estado);
            Assert.Contains(motor.Auditorias, x => x.Evento == "envio_iniciado");
            Assert.Equal(CicloMotorIntegracaoResultado.NenhumTrabalho,
                await worker.ExecutarUmCicloAsync(CancellationToken.None));
            Assert.Single(sap.Recebidas);
        }
    }

    [Fact]
    public async Task Nova_instancia_processa_snapshot_sem_callback_http()
    {
        var (provider, correlations, motor, _) = await FixtureAsync();
        using (provider)
        {
            await provider.GetRequiredService<ProcessarCallbackGuardianMvpCasoDeUso>()
                .ExecutarAsync(Callback(), CancellationToken.None);
            var persisted = (await correlations.ObterPorProtocoloAsync("PROTO-H6", CancellationToken.None))!;
            var rehydrated = new SnapshotOnlyRepository(persisted with { Callback = null });
            var sap = new PesagemSapMvpGatewayFake();
            var processor = new ProcessadorPesagemSapMotor(rehydrated, sap);
            var work = Assert.Single(motor.Integracoes);
            var claim = new ClaimIntegracaoMotor(work.Id, Guid.NewGuid(), work.CorrelationId,
                work.Origem, work.Destino, work.Operacao, work.ChaveNegocio, work.FingerprintSha256,
                1, "H6-RESTART", DateTimeOffset.UtcNow.AddMinutes(1));

            Assert.Null((await rehydrated.ObterPorProtocoloAsync("PROTO-H6", CancellationToken.None))!.Callback);
            Assert.Null((await processor.ProcessarAsync(claim, CancellationToken.None)).Falha);
            var sent = Assert.Single(sap.Recebidas);
            Assert.Equal(new PesosCallbackSapMvp(sent.PesoBruto, sent.PesoTara, sent.PesoAntesDesconto), persisted.Pesos);
            Assert.Equal(persisted.Entrada.PedidoCompras, sent.PedidoCompras);
            Assert.Equal(persisted.Entrada.ContratoAcmSap, sent.ContratoACM);
        }
    }

    [Fact]
    public async Task Pos_post_incerto_fica_indeterminado_sem_retry_automatico()
    {
        var (provider, correlations, motor, _) = await FixtureAsync(indeterminate: true);
        using (provider)
        {
            var callback = provider.GetRequiredService<ProcessarCallbackGuardianMvpCasoDeUso>();
            await callback.ExecutarAsync(Callback(), CancellationToken.None);
            var worker = provider.GetRequiredService<MotorIntegracaoServico>();
            Assert.Equal(CicloMotorIntegracaoResultado.ResultadoIndeterminado,
                await worker.ExecutarUmCicloAsync(CancellationToken.None));
            Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, Assert.Single(motor.Integracoes).Estado);
            Assert.Equal(CicloMotorIntegracaoResultado.NenhumTrabalho,
                await worker.ExecutarUmCicloAsync(CancellationToken.None));
            Assert.Single(motor.Tentativas);
            Assert.Equal(1, provider.GetRequiredService<UncertainGateway>().Calls);
        }
    }

    [Fact]
    public void Sql_h6_preserva_baseline_e_exige_rollback_sem_dados()
    {
        var root = Root();
        var dir = Path.Combine(root, "docs", "Incrementos", "006");
        var migration = File.ReadAllText(Path.Combine(dir, "MIGRATION_006H6_CALLBACK_PESOS_SAP.sql"));
        var validation = File.ReadAllText(Path.Combine(dir, "VALIDATION_006H6_CALLBACK_PESOS_SAP.sql"));
        var rollback = File.ReadAllText(Path.Combine(dir, "ROLLBACK_006H6_CALLBACK_PESOS_SAP.sql"));
        foreach (var name in new[] { "peso_bruto", "tara_final", "peso_liquido" })
        {
            Assert.Contains($"add column {name} numeric null", migration, StringComparison.Ordinal);
            Assert.Contains(name, validation, StringComparison.Ordinal);
            Assert.Contains($"drop column {name}", rollback, StringComparison.Ordinal);
        }
        Assert.Contains("<> 23", migration, StringComparison.Ordinal);
        Assert.Contains("= 26", validation, StringComparison.Ordinal);
        Assert.Contains("begin transaction read only", validation, StringComparison.Ordinal);
        Assert.Contains("rollback;", validation, StringComparison.Ordinal);
        Assert.Contains("raise exception", rollback, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSql_registra_snapshot_e_trabalho_na_mesma_transacao()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "FugaTES.Persistencia", "Recebimento",
            "RepositorioCorrelacaoGuardianSapPostgreSql.cs"));
        var begin = source.IndexOf("public async Task<ResultadoRegistroCallbackMvp> RegistrarEEnfileirarAsync", StringComparison.Ordinal);
        var end = source.IndexOf("public async Task<ResultadoRegistroSnapshotMvp>", begin, StringComparison.Ordinal);
        var method = source[begin..end];
        Assert.Contains("new NpgsqlCommand(RegisterCallbackWorkSql, connection, transaction)", method, StringComparison.Ordinal);
        Assert.Contains("RegistrarCandidatoNaTransacaoAsync(\n                connection, transaction", method, StringComparison.Ordinal);
        var register = method.IndexOf("RegistrarCandidatoNaTransacaoAsync", StringComparison.Ordinal);
        Assert.True(register < method.IndexOf("transaction.CommitAsync", register, StringComparison.Ordinal));
        Assert.Contains("transaction.RollbackAsync", method, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_generico_rejeita_operacao_sap_e_runtime_desabilitado_falha_fechado()
    {
        var claim = new ClaimIntegracaoMotor(Guid.NewGuid(), Guid.NewGuid(), "DOC-H6",
            "GUARDIAN", "SAP", GuardianPesagemSapMotorWork.Operacao, "PROTO-H6", new string('A', 64),
            1, "H6", DateTimeOffset.UtcNow.AddMinutes(1));
        var fakeResult = await new ProcessadorExternoIntegracaoFake().ProcessarAsync(claim, CancellationToken.None);
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, fakeResult.Falha);
        var services = new ServiceCollection();
        services.AddSapPesagemRuntime(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var disabled = provider.GetRequiredService<IPesagemSapMvpGateway>();
        Assert.IsType<PesagemSapMvpGatewayDesabilitado>(disabled);
        var failure = await Assert.ThrowsAsync<FalhaPesagemSapMvpException>(() =>
            disabled.EnviarAsync(new PesagemSapMvp("Toledo", "C", "E", "T", 1, "TR", "M", "BP",
                1m, 0m, 1m, "NF", "PO", "DOC", 1, "ACM"), CancellationToken.None));
        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, failure.Classificacao);
    }

    [Fact]
    public void Sap_habilitado_sem_auth_autoritativa_falha_na_composicao()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sap:Enabled"] = "true", ["Sap:BaseUrl"] = "https://sap.invalid/",
            ["Sap:SapClient"] = "110", ["Sap:Timeout"] = "00:00:05"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSapPesagemRuntime(configuration));
    }

    [Fact]
    public async Task Host_guardian_roteia_operacao_sap_sem_abrir_datasource()
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostIntegracao:PollInterval"] = "00:00:01",
            ["HostIntegracao:ShutdownGracePeriod"] = "00:00:05",
            ["HostIntegracao:ExternalMode"] = "Guardian",
            ["HostIntegracao:PersistenceMode"] = "PostgreSql",
            ["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=/synthetic/not-used/pgpass",
            ["AcmR57:Enabled"] = "true",
            ["MotorIntegracao:LeaseDuration"] = "00:01:00",
            ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
            ["MotorIntegracao:RetryBackoff"] = "00:05:00",
            ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:45",
            ["Guardian:BaseUrl"] = "https://guardian.invalid",
            ["Guardian:Timeout"] = "00:00:10",
            ["Guardian:PreCadastroEndpoint"] = "/api/v1/Guardian/ProtocoloPreCadastro",
            ["Guardian:Email"] = "test-user",
            ["Guardian:Password"] = "test-credential",
            ["Guardian:Signature"] = "test-signature"
        });
        HostIntegracaoComposition.ConfigurarServicos(builder.Services, builder.Configuration);
        var correlations = new RepositorioCorrelacaoRecebimentoMemoria();
        await correlations.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        await correlations.AssociarContratoAcmAsync("DOC-H6", "ACM-H6", CancellationToken.None);
        await correlations.AssociarProtocoloAsync("DOC-H6", "PROTO-H6", CancellationToken.None, DateTimeOffset.UnixEpoch);
        await correlations.RegistrarCallbackAsync(Callback(), CancellationToken.None);
        var sap = new PesagemSapMvpGatewayFake();
        var ticketEvidence = new UnexpectedAcmTicketEvidenceStore();
        builder.Services.AddScoped<IRepositorioCorrelacaoRecebimento>(_ => correlations);
        builder.Services.AddSingleton<IAcmSapTicketEvidenceStore>(ticketEvidence);
        builder.Services.AddScoped<IPesagemSapMvpGateway>(_ => sap);
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var callbackFingerprint = CallbackGuardianMvpFingerprint.Calcular(Callback());
        var claim = new ClaimIntegracaoMotor(Guid.NewGuid(), Guid.NewGuid(), "DOC-H6", "GUARDIAN", "SAP",
            GuardianPesagemSapMotorWork.Operacao, "PROTO-H6",
            GuardianPesagemSapMotorWork.Fingerprint("PROTO-H6", callbackFingerprint), 1, "H6",
            DateTimeOffset.UtcNow.AddMinutes(1));

        var result = await scope.ServiceProvider.GetRequiredService<IProcessadorExternoIntegracao>()
            .ProcessarAsync(claim, CancellationToken.None);
        Assert.Null(result.Falha);
        Assert.Single(sap.Recebidas);
        Assert.Equal(0, ticketEvidence.Calls);
    }

    private static async Task<(ServiceProvider, RepositorioCorrelacaoRecebimentoMemoria,
        RepositorioMotorIntegracaoFake, PesagemSapMvpGatewayFake)> FixtureAsync(bool indeterminate = false)
    {
        var services = new ServiceCollection();
        GuardianEngineStageAComposition.ConfigurarOffline(services,
            new PreCadastroGuardianSet26GatewayFake("unused"),
            new GuardianSet26Options("/fake", "/fake"));
        services.AddSingleton<RepositorioCorrelacaoRecebimentoMemoria>();
        services.AddSingleton<IRepositorioCorrelacaoRecebimento>(sp => sp.GetRequiredService<RepositorioCorrelacaoRecebimentoMemoria>());
        services.AddSingleton<IRegistroCallbackPesagemSapStore, RegistroCallbackPesagemSapMemoria>();
        services.AddSingleton<PesagemSapMvpGatewayFake>();
        if (indeterminate)
        {
            services.AddSingleton<UncertainGateway>();
            services.AddSingleton<IPesagemSapMvpGateway>(sp => sp.GetRequiredService<UncertainGateway>());
        }
        else services.AddSingleton<IPesagemSapMvpGateway>(sp => sp.GetRequiredService<PesagemSapMvpGatewayFake>());
        services.AddScoped<ProcessarCallbackGuardianMvpCasoDeUso>();
        services.AddScoped<ProcessadorPesagemSapMotor>();
        services.AddScoped<IProcessadorExternoIntegracao>(sp => sp.GetRequiredService<ProcessadorPesagemSapMotor>());
        var provider = services.BuildServiceProvider();
        var correlations = provider.GetRequiredService<RepositorioCorrelacaoRecebimentoMemoria>();
        await correlations.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        await correlations.AssociarContratoAcmAsync("DOC-H6", "ACM-H6", CancellationToken.None);
        await correlations.AssociarProtocoloAsync("DOC-H6", "PROTO-H6", CancellationToken.None, DateTimeOffset.UnixEpoch);
        return (provider, correlations, provider.GetRequiredService<RepositorioMotorIntegracaoFake>(),
            provider.GetRequiredService<PesagemSapMvpGatewayFake>());
    }

    private static RecebimentoSapCpiMvp Input() => new("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-H6",
        "BP-EMISSOR", "BP-TRANSPORTADORA", "CONTRATO-H6", "DOC-H6", "COMPRA", "CENTRO-H6",
        "ENTRADA", "NF", "PO-H6", 7, "ACM-H6");
    private static CallbackGuardianMvp Callback() => new("PROTO-H6", "TICKET-H6", 1200m, 200m, 1000m, "CONCLUIDO");
    private sealed class UncertainGateway : IPesagemSapMvpGateway
    {
        public int Calls { get; private set; }
        public Task EnviarAsync(PesagemSapMvp pesagem, CancellationToken cancellationToken)
        {
            Calls++;
            throw new FalhaPesagemSapMvpException("Synthetic uncertain POST",
                ClassificacaoFalhaIntegracao.ResultadoIndeterminado);
        }
    }

    private sealed class UnexpectedAcmTicketEvidenceStore : IAcmSapTicketEvidenceStore
    {
        public int Calls { get; private set; }

        public Task PreservarAsync(CorrelacaoRecebimentoMvp correlacao, PesagemSapMvp pesagem, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("The non-ACM H6 fixture must not persist ACM SAP ticket evidence.");
        }
    }

    private sealed class SnapshotOnlyRepository(CorrelacaoRecebimentoMvp snapshot) : IRepositorioCorrelacaoRecebimento
    {
        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocolo, CancellationToken token) =>
            Task.FromResult<CorrelacaoRecebimentoMvp?>(snapshot.ProtocoloGuardian == protocolo ? snapshot : null);
        public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string documento, CancellationToken token) =>
            Task.FromResult<CorrelacaoRecebimentoMvp?>(snapshot.DocumentoSap == documento ? snapshot : null);
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp entrada, CancellationToken token) => throw new NotSupportedException();
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string documento, string acm, CancellationToken token) => throw new NotSupportedException();
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string documento, string protocolo, CancellationToken token, DateTimeOffset criadoEm) => throw new NotSupportedException();
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken token) => throw new NotSupportedException();
    }
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FugaTES.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
