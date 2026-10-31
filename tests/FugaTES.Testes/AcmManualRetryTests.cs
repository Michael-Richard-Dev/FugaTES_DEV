using FugaTES.Aplicacao.Servicos.ManualRetry;

public sealed class AcmManualRetryTests
{
    static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-10-27T20:00:00Z");
    static readonly Guid Id=Guid.Parse("bac31c3d-2a15-4c31-bfaa-b9467c34dc00");
    const string H="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    static AcmRetryActor Actor=>new(true,"S-1-5-21-1-2-3-1001");
    public static AcmRetrySnapshot Failed()=>new(){IntegrationId=Id,Contract="NEW-TEST",Item="10",Version=1,Fingerprint=H,CanonicalFingerprint=H,
        CorrelationId="authorized-inbound",State="AGUARDANDO_ACAO_MANUAL",Fence=1,AttemptId=Guid.NewGuid(),IdentityVerified=true,AttemptOutcome="FALHA",
        JournalStatus="AGUARDANDO_DEPENDENCIA_EXTERNA",Stage="R165U_PRE_SEND_BLOCKED",RetryableFailure=true,JournalUpdatedUtc=Now.AddMinutes(-2)};
    static AcmRetryApproval Approval(AcmRetrySnapshot s)=>new(){Homologated=true,ExecutionAuthorized=true,ExclusiveWorker=true,IntegrationId=s.IntegrationId,Contract=s.Contract,Item=s.Item,Fingerprint=H,ContractMappingHash=H,OperatorSid=Actor.Sid,
        AuthorizationReference="OFFLINE_TEST_APPROVAL",BusinessOriginEvidence="OFFLINE_TEST_AUTHENTICATED_INBOUND",ObservedSnapshotDigest=s.Digest(),NotBeforeUtc=Now.AddMinutes(-1),UntilUtc=Now.AddMinutes(5),AllowedStage=AcmRetryStage.SoapThenRest};
    [Fact]public void Proven_recoverable_pre_dispatch_failure_allows_same_integration()
    {var s=Failed();var d=AcmManualRetryPolicy.Evaluate(s,Approval(s),Actor,Now);Assert.True(d.Eligible);Assert.Equal(AcmRetryStage.SoapThenRest,d.Stage);}
    [Theory][InlineData("CONCLUIDA")][InlineData("EM_PROCESSAMENTO")][InlineData("RESULTADO_INDETERMINADO")][InlineData("PENDENTE")][InlineData("AGUARDANDO_REPROCESSAMENTO")]
    public void Unsafe_motor_states_block(string state)
    {var s=Failed() with {State=state};Assert.False(AcmManualRetryPolicy.Evaluate(s,Approval(s),Actor,Now).Eligible);}
    [Fact]public void SOAP_confirmed_retry_is_REST_only_and_preserves_identity_metadata()
    {
        var s=Failed() with {Stage="R165U_PROTOCOL_BLOCKED",Metadata="U:"+H+"::42",GuardianRequestedCode="NEW-TEST",GuardianConfirmedCode="NEW-TEST",Reference="NEW-REF"};
        var d=AcmManualRetryPolicy.Evaluate(s,Approval(s) with {AllowedStage=AcmRetryStage.RestOnly},Actor,Now);
        Assert.True(d.Eligible);Assert.Equal(AcmRetryStage.RestOnly,d.Stage);Assert.Equal("U:"+H+"::42",d.ResumeMetadata);
    }
    [Fact]public void HTTP400_without_verified_correction_blocks()
    {var s=Failed() with {ErrorCategory="GUARDIAN_REST_HTTP_400",Stage="R165Y_REST_REJECTED"};Assert.False(AcmManualRetryPolicy.Evaluate(s,Approval(s),Actor,Now).Eligible);}
    [Fact]public void Unknown_attempt_or_armed_stage_blocks_even_with_approval()
    {var s=Failed() with {UnsafeAttempt=true,Stage="R165U_REST_ARMED"};Assert.False(AcmManualRetryPolicy.Evaluate(s,Approval(s),Actor,Now).Eligible);}
    [Theory][InlineData("SOAP_TIMEOUT")][InlineData("GUARDIAN_REST_HTTP_408")][InlineData("GUARDIAN_REST_HTTP_504")]
    public void Timeout_marker_blocks_even_when_an_old_classification_flag_is_inconsistent(string category)
    {var s=Failed() with{ErrorCategory=category};Assert.False(AcmManualRetryPolicy.Evaluate(s,Approval(s),Actor,Now).Eligible);}
    [Fact]public void Synthetic_missing_identity_and_business_origin_are_never_eligible()
    {
        var s=Failed();foreach(var row in new[]{s with{Synthetic=true},s with{IdentityVerified=false},s with{CanonicalFingerprint=new string('B',64)}})
            Assert.False(AcmManualRetryPolicy.Evaluate(row,Approval(row),Actor,Now).Eligible);
        Assert.False(AcmManualRetryPolicy.Evaluate(s,Approval(s) with{BusinessOriginEvidence=""},Actor,Now).Eligible);
    }
    [Fact]public void Existing_protocol_missing_SOAP_link_changed_payload_and_stale_window_block()
    {
        var s=Failed();
        Assert.False(AcmManualRetryPolicy.Evaluate(s with{HasProtocolOrCallback=true},Approval(s),Actor,Now).Eligible);
        Assert.False(AcmManualRetryPolicy.Evaluate(s,Approval(s) with{UntilUtc=Now},Actor,Now).Eligible);
        Assert.False(AcmManualRetryPolicy.Evaluate(s,Approval(s) with{Fingerprint=new string('B',64)},Actor,Now).Eligible);
        var rest=s with{Stage="R165U_PROTOCOL_BLOCKED",Metadata="U:"+H+"::42",GuardianRequestedCode="NEW-TEST",Reference="NEW-REF"};
        Assert.False(AcmManualRetryPolicy.Evaluate(rest,Approval(rest) with{AllowedStage=AcmRetryStage.RestOnly},Actor,Now).Eligible);
    }
    [Fact]public void Known_HTTP400_requires_server_evidence_bound_to_this_exact_failure()
    {
        var s=Failed() with{Stage="R165Y_REST_REJECTED",ErrorCategory="GUARDIAN_REST_HTTP_400",RetryableFailure=false,
            Metadata="U:"+H+":"+H+":42",GuardianRequestedCode="NEW-TEST",GuardianConfirmedCode="NEW-TEST",Reference="NEW-REF"};
        var approval=Approval(s) with{AllowedStage=AcmRetryStage.RestOnly,CorrectedFailureDigest=s.Digest(),ExternalCorrectionEvidence="OFFLINE_EXTERNAL_DEPENDENCY_CORRECTED"};
        Assert.True(AcmManualRetryPolicy.Evaluate(s,approval,Actor,Now).Eligible);
        Assert.False(AcmManualRetryPolicy.Evaluate(s with{Fence=2},approval,Actor,Now).Eligible);
    }
    [Theory][InlineData("          ")][InlineData("senha=not-for-journal")][InlineData("short")]
    public void Invalid_or_secret_like_justification_is_rejected(string reason)=>Assert.False(AcmManualRetryService.SafeJustification(reason));

    [Fact]public async Task Admission_waits_for_store_commit_and_passes_authenticated_operator_reason_and_original_identity()
    {
        var s=Failed();var store=new GateStore(s);var service=new AcmManualRetryService(store,new Approvals(Approval(s)),new Clock());
        var request=new AcmRetryRequest(Guid.NewGuid(),s.IntegrationId,s.Digest(),"Dependencia externa corrigida e comprovada",true);
        var pending=service.RequestAsync(request,Actor,default);await store.Entered.Task;
        Assert.False(pending.IsCompleted);Assert.Equal(Actor,store.Actor);Assert.Equal(request,store.Request);
        store.Commit.TrySetResult();var result=await pending;Assert.True(result.Accepted);Assert.Equal(s.IntegrationId,result.IntegrationId);
    }
    [Fact]public async Task Unknown_commit_never_becomes_success_or_dispatch()
    {
        var s=Failed();var store=new GateStore(s){Fail=true};store.Commit.TrySetResult();
        var service=new AcmManualRetryService(store,new Approvals(Approval(s)),new Clock());
        var result=await service.RequestAsync(new(Guid.NewGuid(),s.IntegrationId,s.Digest(),"Dependencia externa corrigida e comprovada",true),Actor,default);
        Assert.False(result.Accepted);Assert.Equal("UNKNOWN",result.State);Assert.Equal(0,store.Starts);
    }
    [Fact]public async Task Unauthenticated_command_never_reaches_store()
    {
        var s=Failed();var store=new GateStore(s);var service=new AcmManualRetryService(store,new Approvals(Approval(s)),new Clock());
        var preview=await service.PreviewAsync(s.IntegrationId,new(false,Actor.Sid),default);
        Assert.False(preview.Decision.Eligible);Assert.False(store.Entered.Task.IsCompleted);
        var result=await service.RequestAsync(new(Guid.NewGuid(),s.IntegrationId,s.Digest(),"Justificativa de teste valida",true),new(false,Actor.Sid),default);
        Assert.Equal("BLOCKED",result.State);Assert.Null(store.Request);
    }
    sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class Approvals(AcmRetryApproval a):IAcmRetryApprovals
    {public Task<AcmRetryApproval?> ResolveAsync(AcmRetryActor actor,Guid id,CancellationToken ct)=>Task.FromResult<AcmRetryApproval?>(a);}
    sealed class GateStore(AcmRetrySnapshot snapshot):IAcmManualRetryStore
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Commit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AcmRetryRequest? Request;public AcmRetryActor? Actor;public bool Fail;public int Starts;
        public Task<AcmRetrySnapshot?> ReadAsync(Guid id,CancellationToken ct)=>Task.FromResult<AcmRetrySnapshot?>(snapshot);
        public async Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest request,AcmRetryActor actor,AcmRetryApproval approval,DateTimeOffset now,CancellationToken ct)
        {Request=request;Actor=actor;Entered.TrySetResult();await Commit.Task;if(Fail)throw new InvalidOperationException("TEST_COMMIT_UNKNOWN");return new("QUEUED","TEST_COMMITTED",request.CommandId,request.IntegrationId);}
        public Task<AcmRetryWork?> StartAsync(Guid commandId,AcmRetryApproval a,string worker,DateTimeOffset now,CancellationToken ct){Starts++;return Task.FromResult<AcmRetryWork?>(null);}
        public Task CompleteAsync(AcmRetryWork w,string result,DateTimeOffset now,CancellationToken ct)=>Task.CompletedTask;
        public Task<Guid?> PendingCommandAsync(AcmRetryApproval a,CancellationToken ct)=>Task.FromResult<Guid?>(null);
    }
}
