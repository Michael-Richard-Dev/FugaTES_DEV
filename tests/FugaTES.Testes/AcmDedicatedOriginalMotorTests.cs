using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Callback.Http.ManualRetry;
using FugaTES.Infraestrutura.ContratosSapToledo;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class AcmDedicatedOriginalMotorTests
{
    const string Sid = "S-1-5-21-1-2-3-1001";
    static AcmManualRetryCommandProfile Profile() => new()
    {
        BaseConfigurationFile = Path.GetFullPath("base.json"), CredentialFile = Path.GetFullPath("command.key"),
        ApprovalsFile = Path.GetFullPath("approvals.json"), Passfile = Path.GetFullPath("pgpass"), NormalConfigurationFile = Path.GetFullPath("normal.json"),
        OperatorSid = Sid, WorkerIdentity = "offline-dedicated-original-motor", Contract = "1234", Item = "10"
    };
    static AcmRetryApproval Approval(Guid id, string fingerprint) => new()
    {
        IntegrationId = id, Contract = "1234", Item = "10", OperatorSid = Sid, Fingerprint = fingerprint,
        ObservedSnapshotDigest = new('B', 64), ContractMappingHash = new('C', 64), Homologated = true,
        ExecutionAuthorized = true, ExclusiveWorker = true, AuthorizationReference = "OFFLINE_UNIT_APPROVAL",
        BusinessOriginEvidence = "OFFLINE_BUSINESS_EVIDENCE", AllowedStage = AcmRetryStage.RestOnly,
        NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-1), UntilUtc = DateTimeOffset.UtcNow.AddMinutes(1)
    };

    [Fact]
    public void Normal_promotion_is_in_memory_and_bound_to_one_approved_target_without_inventing_evidence()
    {
        var profile = Profile(); var approval = Approval(Guid.NewGuid(), new('A', 64));
        var normal = new AcmR57NormalOptions
        {
            Enabled = false, ExecutionAuthorized = false, AuthorizationReference = "LEGACY_DISABLED",
            GuardianEnvironment = "HML", GuardianIdentityEvidence = "OFFLINE_GUARDIAN_IDENTITY",
            PostgreSqlIdentityEvidence = "OFFLINE_PG_IDENTITY", JournalEvidence = "OFFLINE_JOURNAL",
            IdentifierMappingEvidence = "OFFLINE_TARGET_MAPPING", SapContractCopiedVerbatimApproved = true,
            ApprovedPartnerMapping = new() { NumeroContrato = "1234", Item = "10", Centro = "3011", SapPartnerId = "SAP-P", GuardianCode = "G-P", EvidenceReference = "OFFLINE_PARTNER" },
            ApprovedMaterialMapping = new() { NumeroContrato = "1234", Item = "10", Centro = "3011", SapMaterial = "SAP-M", GuardianCode = "G-M", EvidenceReference = "OFFLINE_MATERIAL" }
        };
        var prepared = AcmManualRetryCommandHost.ApproveNormal(profile, approval, normal);
        Assert.True(prepared.Enabled); Assert.True(prepared.ExecutionAuthorized); Assert.Equal("OFFLINE_UNIT_APPROVAL", prepared.AuthorizationReference);
        Assert.Same(normal.ApprovedPartnerMapping, prepared.ApprovedPartnerMapping); Assert.Same(normal.ApprovedMaterialMapping, prepared.ApprovedMaterialMapping);
        Assert.Equal("OFFLINE_GUARDIAN_IDENTITY", prepared.GuardianIdentityEvidence); Assert.False(prepared.AcmInboundWindow.Enabled);
        Assert.False(normal.Enabled); Assert.False(normal.ExecutionAuthorized); Assert.Equal("LEGACY_DISABLED", normal.AuthorizationReference);
        Assert.Throws<InvalidOperationException>(() => AcmManualRetryCommandHost.ApproveNormal(profile, approval, normal with { ApprovedPartnerMapping = normal.ApprovedPartnerMapping with { NumeroContrato = "1139" } }));
        Assert.Throws<InvalidOperationException>(() => AcmManualRetryCommandHost.ApproveNormal(profile, approval, normal with { GuardianIdentityEvidence = "" }));
    }

    [Fact]
    public async Task Admission_commit_wakes_original_worker_and_motor_once_and_replay_never_dispatches_twice()
    {
        var profile = Profile(); var repository = new RepositorioMotorIntegracaoFake();
        var versions = new ContratoSapToledoVersaoStoreFake(repository);
        var snapshot = new ContratoSapToledoSnapshot("Toledo", "1234", "AC3P", "Descricao", "11/09/2026", "11/09/2026", "31/12/2026",
            10, "31/12/2026", "T", 100000m, 10000m, "0020000051", "000000000007000001", "E", "0000000042", "001", "001", "offline", 1.5m, "3011", "10", Array.Empty<string?>()) { Item = "10" };
        var canonical = new ContratoSapToledoCanonicalizer().Canonicalizar(snapshot);
        var version = await versions.RegistrarOuObterAsync(snapshot, canonical.FingerprintSha256, canonical.PayloadCanonicoJson, "OFFLINE_COMMAND", default);
        var approval = Approval(version.IntegracaoId, canonical.FingerprintSha256); var processor = new OfflineProcessor();
        var motor = new MotorIntegracaoServico(repository, processor, new DetectorCandidatosIntegracaoFake(), new ValidadorGenericoIntegracao(),
            new FixedIdentity(), new FixedTimeout(), TimeProvider.System, new MotorIntegracaoOptions
            {
                LeaseDuration = TimeSpan.FromMinutes(1), LeaseRenewInterval = TimeSpan.FromSeconds(10), RetryBackoff = TimeSpan.FromMinutes(1),
                AcmPilotNumeroContrato = "1234", AcmPilotItem = "10", AcmPilotCentro = "3011"
            }, versions);
        var store = new CommitStore(); var queue = new AcmRetryDispatchQueue(); var decorated = new CommittedAcmRetryStore(store, queue);
        await using var services = new ServiceCollection().AddSingleton(motor).AddSingleton<TimeProvider>(TimeProvider.System)
            .AddScoped<IAcmManualRetryStore>(_ => decorated).BuildServiceProvider();
        var executor = new AcmApprovedRetryExecutor(services.GetRequiredService<IServiceScopeFactory>(), profile);
        using var hosted = new AcmRetryCommandWorker(queue, executor, NullLogger<AcmRetryCommandWorker>.Instance);
        var commandService = new AcmManualRetryService(decorated, new StaticAcmRetryApprovals(profile, [approval]), TimeProvider.System);
        var request = new AcmRetryRequest(Guid.NewGuid(), version.IntegracaoId, approval.ObservedSnapshotDigest, "Justificativa offline comprovada", true);
        await hosted.StartAsync(default);
        var pending = commandService.RequestAsync(request, new(true, Sid), default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted); Assert.Empty(repository.Tentativas); Assert.Equal(0, store.Starts); Assert.Equal(0, processor.Calls);
        store.Commit.TrySetResult(); Assert.True((await pending).Accepted);
        Assert.Equal("Processado", await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ALREADY_RECORDED", (await commandService.RequestAsync(request, new(true, Sid), default)).State);
        await hosted.StopAsync(default);
        Assert.Equal(1, store.Starts); Assert.Equal(1, processor.Calls); Assert.Single(repository.Tentativas);
        Assert.Equal(version.IntegracaoId, processor.Target); Assert.Equal(profile.WorkerIdentity, processor.Worker);
        Assert.Equal(0, store.Polls);
    }
    sealed class FixedIdentity : IIdentidadeWorkerProvider { public IdentidadeWorkerIntegracao Obter() => new("offline-dedicated-original-motor"); }
    sealed class FixedTimeout : IPoliticaTimeoutProcessamentoIntegracao { public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim) => TimeSpan.FromSeconds(30); }
    sealed class OfflineProcessor : IProcessadorExternoIntegracao
    {
        public int Calls; public Guid Target; public string? Worker;
        public Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
        { Calls++; Target = claim.IntegracaoId; Worker = claim.Worker; return Task.FromResult(ResultadoProcessamentoExterno.Sucesso); }
    }
    sealed class CommitStore : IAcmManualRetryStore
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts, Polls; bool recorded;
        public async Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest r, AcmRetryActor a, AcmRetryApproval p, DateTimeOffset n, CancellationToken ct)
        {
            if (recorded) return new("ALREADY_RECORDED", "READ_EXISTING_COMMAND_NO_RESEND", r.CommandId, r.IntegrationId);
            Entered.TrySetResult(); await Commit.Task.WaitAsync(ct); recorded = true;
            return new("QUEUED", "OFFLINE_COMMITTED", r.CommandId, r.IntegrationId);
        }
        public Task<AcmRetryWork?> StartAsync(Guid id, AcmRetryApproval a, string w, DateTimeOffset n, CancellationToken ct)
        { if (!recorded) throw new InvalidOperationException("COMMIT_REQUIRED"); Starts++; return Task.FromResult<AcmRetryWork?>(new(id, a.IntegrationId, 0, w)); }
        public Task CompleteAsync(AcmRetryWork w, string r, DateTimeOffset n, CancellationToken ct) { Completed.TrySetResult(r); return Task.CompletedTask; }
        public Task<AcmRetrySnapshot?> ReadAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("Unexpected preview");
        public Task<Guid?> PendingCommandAsync(AcmRetryApproval a, CancellationToken ct) { Polls++; throw new InvalidOperationException("NO_GLOBAL_POLL"); }
    }
}

