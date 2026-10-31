using FugaTES.Aplicacao.CasosDeUso.Mvp;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Infraestrutura.Recebimento;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class MvpApplicationOrchestrationTests
{
    [Fact]
    public async Task Host_router_encaminha_ACM_ao_use_case_e_replay_nao_repete_envio()
    {
        var f = new Fixture();
        var useCase = f.AcmContract();
        var identity = Guid.NewGuid().ToString();
        await f.Run(useCase, identity, viaHostRouter: true);
        await f.Run(useCase, identity, viaHostRouter: true);
        Assert.Equal(1, f.ContractCalls);
        Assert.Equal(StatusFluxoMvp.Concluido, f.Store.Results.Values.Single().Status);
        Assert.Single(f.Motor.Tentativas);
    }

    [Fact]
    public async Task Host_router_encaminha_pesagem_ACM_e_preserva_confirmacao_duravel()
    {
        var f = new Fixture();
        await f.SeedWeighing();
        var useCase = f.AcmWeighing();
        await f.Run(useCase, "DOC-1", viaHostRouter: true);
        await f.Run(useCase, "DOC-1", viaHostRouter: true);
        Assert.Equal(1, f.WeighingCalls);
        Assert.Equal(60m, f.LastWeighing!["PesoLiquido"]);
        Assert.Equal(StatusFluxoMvp.Concluido, f.Store.Results.Values.Single().Status);
        Assert.Single(f.Motor.Tentativas);
    }

    [Theory]
    [InlineData("NO_ACTION", 0, 0)]
    [InlineData("CREATE", 0, 0)]
    [InlineData("PATCH", 0, 0)]
    public async Task Sd_pipeline_usa_motor_e_replay_nao_duplica(string action, int creates, int patches)
    {
        var f = new Fixture { DeliveryAction = action };
        var useCase = f.Sd();
        await f.Run(useCase, "SO-1");
        Assert.Equal(creates, f.CreateCalls);
        Assert.Equal(patches, f.PatchCalls);
        Assert.Equal(1, f.GuardianCalls);
        Assert.Equal("PROTOCOL-1", f.Store.Results.Values.Single().ProtocoloGuardian);
        Assert.Equal("TICKET-RESPONSE-9", f.Store.Results.Values.Single().NumeroTicketGuardian);
        Assert.Equal("SO-1", f.Store.Results.Values.Single().SalesOrder);
        Assert.Equal(action == "CREATE" ? null : "DEL-1", f.Store.Results.Values.Single().DeliveryDocument);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback, f.Store.Results.Values.Single().Status);
        Assert.Equal(["SalesOrder", "Partner", "Partner", "Product", "Delivery", "Guardian"], f.Reads.Append("Guardian"));
        await f.Run(useCase, "SO-1");
        Assert.Single(f.Motor.Integracoes);
        Assert.Single(f.Motor.Tentativas);
        Assert.Equal(1, f.GuardianCalls);
    }

    [Theory]
    [InlineData("SD_TRIGGER")]
    [InlineData("SD_PARTNER_MAPPING")]
    [InlineData("SD_DELIVERY_ACTION")]
    [InlineData("SD_NUMERO_REFERENCIA")]
    [InlineData("SD_CODIGO_CONTRATO")]
    [InlineData("SD_GUARDIAN_FLOW_LITERAL")]
    public async Task Sd_decisao_ausente_bloqueia_antes_de_qualquer_capability(string missing)
    {
        var f = new Fixture { Missing = missing };
        await f.Run(f.Sd(), "SO-1");
        var result = f.Store.Results.Values.Single();
        Assert.Equal(StatusFluxoMvp.AguardandoDecisaoFuncional, result.Status);
        Assert.Equal(missing, result.DecisaoPendente);
        Assert.Empty(f.Reads);
        Assert.Single(f.Motor.Integracoes);
        Assert.Equal(0, f.TotalWrites);
    }

    [Fact]
    public async Task Guardian_dependency_missing_impede_delivery_create()
    {
        var f = new Fixture { DeliveryAction = "CREATE", MissingGuardianDependency = true };
        await f.Run(f.Sd(), "SO-1");
        Assert.Equal(0, f.TotalWrites);
        Assert.Equal(StatusFluxoMvp.AguardandoDecisaoFuncional, f.Store.Results.Values.Single().Status);
    }

    [Theory]
    [InlineData("SC")]
    [InlineData("ROLE")]
    [InlineData("SHIPPING_POINT")]
    [InlineData("ACM_CONTEXT")]
    [InlineData("CONTRACT_MAPPING")]
    public async Task Regra_confirmada_sem_contexto_persiste_pendencia_sem_efeito(string gap)
    {
        var f = new Fixture { RuleGap = gap, DeliveryAction = "CREATE" };
        await f.Run(f.Sd(), "SO-1");
        var result = f.Store.Results.Values.Single();
        Assert.Equal(StatusFluxoMvp.AguardandoDecisaoFuncional, result.Status);
        Assert.NotNull(result.DecisaoPendente);
        Assert.Equal(0, f.TotalWrites);
    }

    [Fact]
    public async Task Mm_preCadastro_aguarda_callback_sem_lancar_101()
    {
        var f = new Fixture();
        await f.Run(f.Mm(), "PO-1");
        Assert.Equal(["PurchaseOrder", "Partner", "Product"], f.Reads);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback, f.Store.Results.Values.Single().Status);
        Assert.Equal(1, f.GuardianCalls);
        Assert.Equal(0, f.MovementCalls);
        Assert.Equal("PO-1", f.Store.Results.Values.Single().PurchaseOrder);
        Assert.Equal("PROTOCOL-1", f.Store.Results.Values.Single().ProtocoloGuardian);
        Assert.Equal("TICKET-RESPONSE-9", f.Store.Results.Values.Single().NumeroTicketGuardian);
    }

    [Fact]
    public async Task Replay_preserva_protocolo_e_ticket_confirmados_sem_novo_PreCadastro()
    {
        var f = new Fixture();
        var useCase = f.Mm();
        await f.Run(useCase, "PO-1");
        var integration = f.Motor.Integracoes.Single();
        var previous = f.Store.Results[integration.Id];
        var claim = new ClaimIntegracaoMotor(integration.Id, Guid.NewGuid(), previous.Correlacao,
            useCase.Origem, useCase.Destino, useCase.Operacao, "PO-1", useCase.Fingerprint("PO-1"),
            2, "TEST", DateTimeOffset.UtcNow.AddMinutes(1));

        var replay = await useCase.ExecutarAsync(claim, CancellationToken.None);

        Assert.Equal(previous, replay);
        Assert.Equal("PROTOCOL-1", replay.ProtocoloGuardian);
        Assert.Equal("TICKET-RESPONSE-9", replay.NumeroTicketGuardian);
        Assert.Equal(1, f.GuardianCalls);
        Assert.Equal(previous, f.Store.Results[integration.Id]);
    }

    [Fact]
    public async Task Claims_concorrentes_do_INC005_produzem_um_unico_envio()
    {
        var f = new Fixture();
        var useCase = f.Mm();
        await Task.WhenAll(f.Run(useCase, "PO-1"), f.Run(useCase, "PO-1"));
        Assert.Single(f.Motor.Integracoes);
        Assert.Single(f.Motor.Tentativas);
        Assert.Equal(1, f.GuardianCalls);
    }

    [Fact]
    public async Task Cancelamento_antes_do_registro_nao_cria_trabalho()
    {
        var f = new Fixture();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Mm().RegistrarAsync("PO-1", source.Token));
        Assert.Empty(f.Motor.Integracoes);
        Assert.Empty(f.Reads);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Mm_101_exige_pesagem_final_e_gatilho_positivo(bool allowed, int calls)
    {
        var f = new Fixture { MovementAllowed = allowed };
        await f.SeedWeighing();
        var useCase = f.Mm101();
        await f.Run(useCase, "DOC-1");
        await f.Run(useCase, "DOC-1");
        Assert.Equal(calls, f.MovementCalls);
        Assert.Equal(StatusFluxoMvp.Concluido, f.Store.Results.Values.Single().Status);
        var result = f.Store.Results.Values.Single();
        Assert.Equal("PO-1", result.PurchaseOrder);
        Assert.Equal(allowed ? "MD-1" : null, result.MaterialDocument);
        Assert.Equal(allowed ? "2026" : null, result.MaterialDocumentYear);
    }

    [Fact]
    public async Task Mm_101_sem_callback_nao_envia()
    {
        var f = new Fixture { MovementAllowed = true };
        await f.Run(f.Mm101(), "DOC-1");
        Assert.Equal(0, f.MovementCalls);
        Assert.Equal("MM_FINAL_WEIGHING_MISSING", f.Store.Results.Values.Single().Erro);
    }

    [Fact]
    public async Task Mm_trigger_desconhecido_nao_consulta_nem_envia()
    {
        var f = new Fixture { Missing = "MM_TRIGGER_MOVIMENTO_101" };
        await f.Run(f.Mm101(), "DOC-1");
        var result = f.Store.Results.Values.Single();
        Assert.Equal(StatusFluxoMvp.AguardandoDecisaoFuncional, result.Status);
        Assert.Empty(f.Reads);
        Assert.Equal(0, f.TotalWrites);
    }

    [Fact]
    public async Task Acm_inbound_duravel_mapeia_cadastra_e_replay_nao_repete()
    {
        var f = new Fixture();
        var useCase = f.AcmContract();
        var identity = Guid.NewGuid().ToString();
        await f.Run(useCase, identity);
        await f.Run(useCase, identity);
        Assert.Equal(1, f.ContractCalls);
        Assert.Equal("CON-1", f.Store.Results.Values.Single().ResultadoFuncional);
    }

    [Theory]
    [InlineData("ACM_ESTADO")]
    [InlineData("ACM_TIPO_LANCAMENTO")]
    [InlineData("ACM_VALOR_PROXIMIDADE_FIM")]
    [InlineData("ACM_CREATE_UPDATE_BEHAVIOR")]
    public async Task Acm_contrato_com_regra_pendente_nao_envia(string missing)
    {
        var f = new Fixture { Missing = missing };
        await f.Run(f.AcmContract(), Guid.NewGuid().ToString());
        var result = f.Store.Results.Values.Single();
        Assert.Equal(missing, result.DecisaoPendente);
        Assert.Equal(0, f.ContractCalls);
    }

    [Fact]
    public async Task Acm_evento5_mapeia_pesos_e_persiste_retorno_sem_replay()
    {
        var f = new Fixture();
        await f.SeedWeighing();
        var useCase = f.AcmWeighing();
        await f.Run(useCase, "DOC-1");
        await f.Run(useCase, "DOC-1");
        Assert.Equal(100m, f.LastWeighing!["PesoBruto"]);
        Assert.Equal(40m, f.LastWeighing["Tara"]);
        Assert.Equal(60m, f.LastWeighing["PesoLiquido"]);
        Assert.Equal(1, f.WeighingCalls);
        Assert.Equal(StatusFluxoMvp.Concluido, f.Store.Results.Values.Single().Status);
    }

    [Theory]
    [InlineData("ACM_TIPO_OPERACAO_SAP")]
    [InlineData("ACM_CONTRATO_ACM_TRANSFORMATION")]
    public async Task Acm_pesagem_pendente_nao_envia(string missing)
    {
        var f = new Fixture { Missing = missing };
        await f.Run(f.AcmWeighing(), "DOC-1");
        var result = f.Store.Results.Values.Single();
        Assert.Equal(missing, result.DecisaoPendente);
        Assert.Equal(0, f.WeighingCalls);
    }

    [Fact]
    public async Task Acm_out_exige_capability_API7_mesmo_com_decisao_injetada()
    {
        var f = new Fixture();
        await f.Run(f.AcmOut(), "ACM-OUT-1");
        Assert.Equal(StatusFluxoMvp.AguardandoDependenciaExterna, f.Store.Results.Values.Single().Status);
        Assert.Equal("API7", f.Store.Results.Values.Single().Erro);
        Assert.Equal(0, f.TotalWrites);
    }

    [Fact]
    public async Task Falha_apos_side_effect_fica_indeterminada_sem_retry_e_sem_secret()
    {
        var f = new Fixture { ThrowAfterGuardian = true };
        var useCase = f.Mm();
        await f.Run(useCase, "PO-1");
        await f.Run(useCase, "PO-1");
        Assert.Equal(1, f.GuardianCalls);
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, f.Motor.Integracoes.Single().Estado);
        Assert.DoesNotContain("SECRET_SENTINEL", f.Store.Results.Values.Single().ToString(), StringComparison.Ordinal);
        Assert.True(f.Store.Results.Values.Single().ErroResultadoIndeterminado);
        Assert.Equal("RESULTADO_EXTERNO_NAO_COMPROVADO", f.Store.Results.Values.Single().ErroCodigo);
    }

    [Fact]
    public async Task Delivery_comprovada_permanece_duravel_quando_Guardian_falha()
    {
        var f = new Fixture { ThrowAfterGuardian = true };
        await f.Run(f.Sd(), "SO-1");
        var result = f.Store.Results.Values.Single();
        Assert.Equal("SO-1", result.SalesOrder);
        Assert.Equal("DEL-1", result.DeliveryDocument);
        Assert.True(result.ErroResultadoIndeterminado);
        Assert.Null(result.ProtocoloGuardian);
        Assert.Equal(0, f.CreateCalls);
    }

    [Fact]
    public async Task Claim_replay_sem_projecao_persiste_indeterminado_sem_efeito()
    {
        var f = new Fixture();
        var useCase = f.Mm();
        var claim = new ClaimIntegracaoMotor(Guid.NewGuid(), Guid.NewGuid(), "CORR-1", useCase.Origem,
            useCase.Destino, useCase.Operacao, "PO-1", useCase.Fingerprint("PO-1"), 2, "TEST", DateTimeOffset.UtcNow.AddMinutes(1));
        var result = await useCase.ExecutarAsync(claim, CancellationToken.None);
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado, result.Status);
        Assert.True(f.Store.Results[claim.IntegracaoId].ErroResultadoIndeterminado);
        Assert.Equal(0, f.TotalWrites);
    }

    [Fact]
    public async Task Resultado_terminal_duravel_precede_decisoes_em_replay()
    {
        var f = new Fixture { Missing = "MM_PARTNER_MAPPING" };
        var useCase = f.Mm();
        var claim = new ClaimIntegracaoMotor(Guid.NewGuid(), Guid.NewGuid(), "CORR-1", useCase.Origem,
            useCase.Destino, useCase.Operacao, "PO-1", useCase.Fingerprint("PO-1"), 2, "TEST", DateTimeOffset.UtcNow.AddMinutes(1));
        var terminal = new ResultadoFluxoMvp(StatusFluxoMvp.AguardandoCallback, "CORR-1", useCase.Operacao,
            PurchaseOrder: "PO-1", ProtocoloGuardian: "PROTOCOL-1");
        f.Store.Results[claim.IntegracaoId] = terminal;
        Assert.Equal(terminal, await useCase.ExecutarAsync(claim, CancellationToken.None));
        Assert.Equal(0, f.TotalWrites);
    }

    [Fact]
    public async Task Projecao_falhando_apos_envio_nao_declara_sucesso()
    {
        var f = new Fixture();
        f.Store.Reject = true;
        await f.Run(f.Mm(), "PO-1");
        Assert.Equal(1, f.GuardianCalls);
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, f.Motor.Integracoes.Single().Estado);
        var integration = f.Motor.Integracoes.Single();
        Assert.False(await f.Motor.ReprocessarAsync(integration.Id, DateTimeOffset.UtcNow, integration.CorrelationId, CancellationToken.None));
        f.Store.Reject = false;
        await f.Run(f.Mm(), "PO-1");
        Assert.Equal(1, f.GuardianCalls);
        Assert.Equal(EstadoIntegracao.ResultadoIndeterminado, f.Motor.Integracoes.Single().Estado);
    }

    [Fact]
    public async Task Composition_default_bloqueia_API7_sem_adapters_reais()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMotorIntegracaoRepository>(new RepositorioMotorIntegracaoFake());
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddMvpOrchestration();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Task.CompletedTask;
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ProcessarAcmSaida>());
        Assert.Null(scope.ServiceProvider.GetService<IConsultaOrdemVendaSap>());
        Assert.Null(scope.ServiceProvider.GetService<IPreCadastroGuardianSet26Gateway>());
    }

    private sealed class Fixture : IDecisoesFluxoMvp, IConsultaOrdemVendaSap, IConsultaPedidoCompraSap,
        IConsultaPartnerSap, IConsultaMaterialSap, IRemessaSap, IDecisaoRemessaSap,
        IPreparacaoSaidaSdMvp, IPreparacaoEntradaMmMvp, IPreCadastroGuardianSet26Gateway,
        IGatilhoMovimentoMaterialSap, IMovimentoMaterialSap, IMapeadorContratoAcmGuardian,
        IGuardianContratoSincronizacaoGateway, IContratoSapToledoVersaoStore, IMapeadorPesagemAcmSap, IReceberPesagemSap
    {
        public RepositorioMotorIntegracaoFake Motor { get; } = new();
        public Projection Store { get; } = new();
        public RepositorioCorrelacaoRecebimentoMemoria Correlations { get; } = new();
        public List<string> Reads { get; } = [];
        public string? Missing { get; init; }
        public string? RuleGap { get; init; }
        public string DeliveryAction { get; init; } = "NO_ACTION";
        public bool MissingGuardianDependency { get; init; }
        public bool MovementAllowed { get; init; }
        public bool ThrowAfterGuardian { get; init; }
        public int GuardianCalls, CreateCalls, PatchCalls, MovementCalls, ContractCalls, WeighingCalls;
        public int TotalWrites => GuardianCalls + CreateCalls + PatchCalls + MovementCalls + ContractCalls + WeighingCalls;
        public IReadOnlyDictionary<string, object?>? LastWeighing;
        public ProcessarSaidaSd Sd() => new(Motor, Store, this, TimeProvider.System, this, this, this, this, this, this, this);
        public ProcessarEntradaMm Mm() => new(Motor, Store, this, TimeProvider.System, this, this, this, this, this);
        public ProcessarMovimento101Mm Mm101() => new(Motor, Store, this, TimeProvider.System, Correlations, this, this, this);
        public ProcessarContratoAcm AcmContract() => new(Motor, Store, this, TimeProvider.System, this, this, this);
        public ProcessarPesagemAcm AcmWeighing() => new(Motor, Store, this, TimeProvider.System, Correlations, this, this);
        public ProcessarAcmSaida AcmOut() => new(Motor, Store, this, TimeProvider.System);

        public async Task Run(FluxoMvpCasoDeUso useCase, string identity, bool viaHostRouter = false)
        {
            await useCase.RegistrarAsync(identity, CancellationToken.None);
            var services = new ServiceCollection();
            services.AddSingleton(new ProcessadorPreCadastroGuardianMotor(Correlations, this));
            services.AddSingleton(new ProcessadorPesagemSapMotor(Correlations,
                new FugaTES.Integracao.SAP.Http.PesagemSapMvpGatewayDesabilitado()));
            services.AddSingleton(new ProcessadorFluxosMvp([useCase]));
            services.AddTransient<ProcessadorExternoIntegracaoRoteado>();
            using var provider = services.BuildServiceProvider();
            IProcessadorExternoIntegracao processor = viaHostRouter
                ? provider.GetRequiredService<ProcessadorExternoIntegracaoRoteado>() : useCase;
            var engine = new MotorIntegracaoServico(Motor, processor, new EmptyDetector(), new ValidadorGenericoIntegracao(), new Worker(), new Timeout(),
                TimeProvider.System, new MotorIntegracaoOptions { LeaseDuration = TimeSpan.FromMinutes(1), LeaseRenewInterval = TimeSpan.FromSeconds(20), RetryBackoff = TimeSpan.FromMinutes(5) });
            await engine.ExecutarUmCicloAsync(CancellationToken.None);
        }

        public void Exigir(string decision) { if (decision == Missing) throw new DecisaoFuncionalPendenteSapException(decision); }
        private void VerifyMarker()
        {
            Assert.False(Motor.TransacaoAtiva);
            Assert.Contains(Motor.Auditorias, audit => audit.Evento == "envio_iniciado" && audit.TentativaId is not null);
        }
        Task<ResultadoConsultaSap<SalesOrderSnapshot>> IConsultaOrdemVendaSap.ConsultarAsync(string id, CancellationToken ct)
        { Reads.Add("SalesOrder"); return Task.FromResult(ResultadoConsultaSap<SalesOrderSnapshot>.Sucesso(new(id, null, "BP-1", null, null,
            [new("10", "MAT-1", 1m, "KG", "CENTRO-1")], RuleGap == "SC" ? [] : [new("SC", "TR-1")], RuleGap == "ACM_CONTEXT" ? null : false))); }
        Task<ResultadoConsultaSap<PurchaseOrderSnapshot>> IConsultaPedidoCompraSap.ConsultarAsync(string id, CancellationToken ct)
        { Reads.Add("PurchaseOrder"); return Task.FromResult(ResultadoConsultaSap<PurchaseOrderSnapshot>.Sucesso(new(id, null, "BP-1", null, [], false))); }
        Task<ResultadoConsultaSap<PartnerSnapshot>> IConsultaPartnerSap.ConsultarAsync(string id, CancellationToken ct)
        { Reads.Add("Partner"); return Task.FromResult(ResultadoConsultaSap<PartnerSnapshot>.Sucesso(new(id, "Partner", 2, [], false, RuleGap == "ROLE" ? [] : ["CRM010"]))); }
        Task<ResultadoConsultaSap<ProductSnapshot>> IConsultaMaterialSap.ConsultarAsync(string id, CancellationToken ct)
        { Reads.Add("Product"); return Task.FromResult(ResultadoConsultaSap<ProductSnapshot>.Sucesso(new(id, null, "KG", null, "Material"))); }
        public Task<ResultadoConsultaSap<DeliverySnapshot>> ObterPorOrdemVendaAsync(string id, CancellationToken ct)
        { Reads.Add("Delivery"); return Task.FromResult(DeliveryAction == "CREATE" ? ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsultaSap.NaoEncontrado) : ResultadoConsultaSap<DeliverySnapshot>.Sucesso(Delivery())); }
        public Task<ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>> ObterCandidatasPorOrdemVendaAsync(string id, CancellationToken ct)
        { Reads.Add("Delivery"); return Task.FromResult(ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Sucesso(DeliveryAction == "CREATE" ? [] : [Delivery()])); }
        public DeliverySnapshot? SelecionarRemessa(SalesOrderSnapshot ordem, IReadOnlyList<DeliverySnapshot> candidatas)
            => FugaTES.Aplicacao.Servicos.Mvp.RegrasFuncionaisMvpConfirmadas.SelecionarRemessa(ordem, candidatas,
                RuleGap == "SHIPPING_POINT" ? new Dictionary<string, string>() : new Dictionary<string, string> { ["CENTRO-1"] = "SP-1" });
        private static DeliverySnapshot Delivery() => new("DEL-1", null, "SP-1", "A", "ETAG-1",
            [new("10", "MAT-1", 1m, "KG", "SO-1", "10")]);
        public Task<ResultadoEscritaSap<DeliverySnapshot>> CriarAsync(CriarRemessaComando command, CancellationToken ct)
        { VerifyMarker(); CreateCalls++; return Task.FromResult(ResultadoEscritaSap<DeliverySnapshot>.Sucesso(Delivery())); }
        public Task<ResultadoEscritaSap<DeliverySnapshot>> AtualizarAsync(string id, string etag, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
        { VerifyMarker(); Assert.Equal("ETAG-1", etag); PatchCalls++; return Task.FromResult(ResultadoEscritaSap<DeliverySnapshot>.Sucesso(Delivery())); }
        public bool DeveCriarRemessa(SalesOrderSnapshot order) => DeliveryAction == "CREATE";
        public bool DeveAtualizarRemessa(DeliverySnapshot delivery) => DeliveryAction == "PATCH";
        public CriarRemessaComando MapearCriacao(SalesOrderSnapshot order) => new(order.SalesOrder, new Dictionary<string, object?> { ["Reference"] = order.SalesOrder });
        public string ResolverPartner(SalesOrderSnapshot order) => "BP-1";
        public string ResolverMaterial(SalesOrderSnapshot order) => "MAT-1";
        public string ResolverSupplier(PurchaseOrderSnapshot order) => "BP-1";
        public string ResolverMaterial(PurchaseOrderSnapshot order) => "MAT-1";
        public IReadOnlyDictionary<string, object?> MapearPatch(SalesOrderSnapshot order, DeliverySnapshot delivery) => new Dictionary<string, object?> { ["Quantity"] = 1m };
        public PreCadastroRecebimentoGuardianSet26 MapearGuardian(SalesOrderSnapshot order, PartnerSnapshot partner, ProductSnapshot? material)
            => PreCadastro() with { CodigoContrato = RuleGap == "CONTRACT_MAPPING" ? "WRONG" : order.SalesOrder };
        public PreCadastroRecebimentoGuardianSet26 MapearGuardian(PurchaseOrderSnapshot order, PartnerSnapshot partner, ProductSnapshot material)
            => PreCadastro() with { CodigoContrato = order.PurchaseOrder };
        private PreCadastroRecebimentoGuardianSet26 PreCadastro() => new(0, "FLOW-EXPLICIT-FAKE", "MAT-1", MissingGuardianDependency ? "" : "BP-1", "TR-1", "CON-1", 1, "REFERENCE-EXPLICIT-FAKE", "https://feedback.invalid", [new(1000, "TYPE"), new(1002, "PLANT"), new(1003, "OPERATION")]);
        public Task<ProtocoloGuardianSet26> EnviarAsync(PreCadastroRecebimentoGuardianSet26 command, CancellationToken ct)
        { VerifyMarker(); GuardianCalls++; if (ThrowAfterGuardian) throw new InvalidOperationException("SECRET_SENTINEL"); return Task.FromResult(new ProtocoloGuardianSet26("PROTOCOL-1", DateTimeOffset.UtcNow, "TICKET-RESPONSE-9")); }
        public bool DeveLancar(PurchaseOrderSnapshot order) => MovementAllowed;
        public MovimentoMaterialComando Mapear(PurchaseOrderSnapshot order) => new(new Dictionary<string, object?> { ["ExplicitFakeMovement"] = "101" });
        public Task<ResultadoEscritaSap<MaterialMovementResult>> LancarAsync(MovimentoMaterialComando command, CancellationToken ct)
        { VerifyMarker(); MovementCalls++; return Task.FromResult(ResultadoEscritaSap<MaterialMovementResult>.Sucesso(new("MD-1", "2026"))); }
        public ContratoGuardianSet26 Mapear(ContratoSapToledoSnapshot snapshot) => new(snapshot.NumeroContrato, "Contract", 1m, "EXPLICIT-FAKE", 0m, "EXPLICIT-FAKE");
        public Task<ConsultaContratoGuardian> ConsultarAsync(string code, CancellationToken ct)
            => Task.FromResult(new ConsultaContratoGuardian(EstadoConsultaContratoGuardian.InexistenteComprovado));
        public async Task<FugaTES.Aplicacao.Contratos.ContratosSapToledo.ResultadoContratoGuardianComResposta> CadastrarComRespostaAsync(ContratoGuardianSet26 contract, CancellationToken ct)
        { await CadastrarAsync(contract, ct); return new(contract.Codigo); }
        public Task<FugaTES.Aplicacao.Contratos.ContratosSapToledo.ResultadoContratoGuardianComResposta> AlterarComRespostaAsync(ContratoGuardianSet26 current, ContratoGuardianSet26 desired, CancellationToken ct)
            => throw new InvalidOperationException("Fixture must not update.");
        public Task CadastrarAsync(ContratoGuardianSet26 contract, CancellationToken ct) { VerifyMarker(); ContractCalls++; return Task.CompletedTask; }
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult<VersaoContratoSapToledo?>(new(ResultadoRegistroVersaoContrato.JaExistente, "CON-1", 1, new string('A', 64), id,
                new(null, "CON-1", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, [])));
        public Task<bool> TemPredecessorNaoTerminalAsync(string contract, long version, CancellationToken ct) => Task.FromResult(false);
        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot, string fingerprint, string payload, string correlation, CancellationToken ct) => throw new NotSupportedException();
        public SapWeighingCommand Mapear(IReadOnlyDictionary<string, object?> weights)
        { LastWeighing = weights; return new("ExplicitFakeOperation", new Dictionary<string, object?> { ["ExplicitFakeMappedWeights"] = weights }); }
        public Task<ResultadoEscritaSap<string?>> EnviarAsync(SapWeighingCommand command, CancellationToken ct)
        { VerifyMarker(); WeighingCalls++; return Task.FromResult(ResultadoEscritaSap<string?>.Sucesso("WEIGHING-1")); }
        public async Task SeedWeighing()
        {
            await Correlations.RegistrarSnapshotAsync(new("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-1", "BP-1", "TR-1", "CON-1", "DOC-1", "TYPE", "PLANT", "IN", "NF", "PO-1", 1, null), CancellationToken.None);
            await Correlations.AssociarProtocoloAsync("DOC-1", "PROTOCOL-1", CancellationToken.None, DateTimeOffset.UtcNow);
            await Correlations.RegistrarCallbackAsync(new("PROTOCOL-1", "TICKET-1", 100m, 40m, 60m, "PesagemFinal"), CancellationToken.None);
        }
    }

    private sealed class Projection : IResultadoFluxoMvpStore
    {
        public Dictionary<Guid, ResultadoFluxoMvp> Results { get; } = [];
        public bool Reject { get; set; }
        public Task ExigirDisponibilidadeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor claim, CancellationToken ct) => Task.FromResult(Results.GetValueOrDefault(claim.IntegracaoId));
        public Task<bool> PersistirAsync(ClaimIntegracaoMotor claim, ResultadoFluxoMvp result, CancellationToken ct)
        { if (!Reject) Results[claim.IntegracaoId] = result; return Task.FromResult(!Reject); }
    }
    private sealed class EmptyDetector : IDetectorCandidatosIntegracao
    { public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CandidatoIntegracao>>([]); }
    private sealed class Worker : IIdentidadeWorkerProvider
    { public IdentidadeWorkerIntegracao Obter() => new("MVP-OFFLINE-TEST"); }
    private sealed class Timeout : IPoliticaTimeoutProcessamentoIntegracao
    { public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim) => TimeSpan.FromSeconds(10); }
}
