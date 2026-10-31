using System.Collections.Immutable;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
using FugaTES.Persistencia.Transportadoras.Fakes;
using FugaTES.Integracao.SAP.BusinessPartner;
namespace FugaTES.DurableSnapshot.R24.Tests;
public class DurableSnapshotR24Tests
{
    static readonly DateTimeOffset Now=new(2026,10,11,12,0,0,TimeSpan.Zero);
    static ContextoSnapshotR24 Context()
    {
        var s=new EscopoSnapshotR24(Guid.NewGuid(),"SAP_HML","110","R20A_V1","");
        return new(s with{ScopeFingerprint=SnapshotFingerprintR24.Scope(s)},VersoesSnapshotR24.PropostaAtual);
    }
    static RegistroBpSnapshotR24 Row(ContextoSnapshotR24 c,string bp="0001",bool enriched=true,string? name=null)
    {
        var r=new RegistroBpSnapshotR24(bp,enriched?AvaliacaoCrm010R24.EVALUATED_VALID:AvaliacaoCrm010R24.INDETERMINATE,Now.AddDays(-30),Now.AddDays(30),name,enriched);
        return r with{RowFingerprint=SnapshotFingerprintR24.Row(c.Versoes,r)};
    }
    static PaginaSnapshotR24 Page(int index,bool terminal,params RegistroBpSnapshotR24[] rows)
    {
        var p=new PaginaSnapshotR24(index,index,1,rows.Length,terminal,rows.ToImmutableArray(),"");return p with{PageFingerprint=SnapshotFingerprintR24.Page(p)};
    }
    sealed class Harness
    {
        public ContextoSnapshotR24 C=Context();public SnapshotMemoryStorageR24 Storage=new();public SnapshotMasterdataMemoryFakeR24 Repo;
        public Harness(){Repo=new(Storage,new Clock(),ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(C.Escopo.ScopeId));}
        public async Task<(Guid Id,PonteiroSnapshotR24 Base,string Hash)> Ready(bool empty=false)
        {
            var id=Guid.NewGuid();var start=await Repo.IniciarTentativaAsync(new(C,id,Now),default);Assert.Equal(SituacaoSnapshotR24.SUCCESS,start.Situacao);
            var rows=empty?Array.Empty<RegistroBpSnapshotR24>():[Row(C)];
            if(!empty)Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await Repo.RegistrarLoteAsync(new(C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(0,false,rows),[]),default)).Situacao);
            int terminal=empty?0:1;Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await Repo.RegistrarLoteAsync(new(C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(terminal,true),[]),default)).Situacao);
            var hash=SnapshotFingerprintR24.Snapshot(C,Now,terminal+1,rows);
            Assert.Equal(EstadoTentativaSnapshotR24.READY,(await Repo.ValidarCompletudeAsync(new(C,id,rows.Length,hash),default)).Dados!.Estado);
            return(id,start.Dados!.Base,hash);
        }
        public Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> Promote((Guid Id,PonteiroSnapshotR24 Base,string Hash) t)=>Repo.PromoverSnapshotAsync(new(C,t.Id,t.Base,t.Hash),default);
        public Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> Read()=>Repo.CarregarSnapshotAtivoAsync(new(C),default);
    }
    [Fact] public async Task ATOMIC_PROMOTION()
    {
        var h=new Harness();var t=await h.Ready();Assert.Null((await h.Read()).Dados);
        var result=await h.Promote(t);Assert.Equal(SituacaoSnapshotR24.SUCCESS,result.Situacao);var active=(await h.Read()).Dados!;
        Assert.Equal(result.Dados!.SnapshotId,active.SnapshotId);Assert.Equal(1,active.Version);Assert.Single(active.Rows);
    }
    [Fact] public async Task PARTIAL_SCAN_NEVER_ACTIVE()
    {
        var h=new Harness();var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(0,false,Row(h.C)),[]),default);
        var fp=SnapshotFingerprintR24.Snapshot(h.C,Now,1,[Row(h.C)]);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Repo.ValidarCompletudeAsync(new(h.C,id,1,fp),default)).Situacao);Assert.Null((await h.Read()).Dados);
    }
    [Theory][InlineData(FalhaPromocaoFakeR24.BeforeCommit)][InlineData(FalhaPromocaoFakeR24.DuringCopy)]
    public async Task PREVIOUS_SNAPSHOT_PRESERVED(FalhaPromocaoFakeR24 fault)
    {
        var h=new Harness();await h.Promote(await h.Ready());var old=(await h.Read()).Dados;var next=await h.Ready();h.Storage.FalharProximaPromocao(h.C.Escopo.ScopeId,fault);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Promote(next)).Situacao);Assert.Equal(old,(await h.Read()).Dados);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task CRASH_RECOVERY(bool enrichment)
    {
        var h=new Harness();await h.Promote(await h.Ready());var old=(await h.Read()).Dados;
        var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(0,false,Row(h.C,enriched:false)),[]),default);
        if(enrichment)await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(1,true),[]),default);
        var repo2=new SnapshotMasterdataMemoryFakeR24(h.Storage,new Clock(),ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(h.C.Escopo.ScopeId));var coordinator=new CoordenadorSnapshotMasterdataR24(repo2);
        var recovery=await coordinator.RecuperarAposReinicioAsync(new(h.C));Assert.Equal(old,recovery.Dados!.Ativo);Assert.Contains(id,recovery.Dados.AbandonedAttempts);Assert.Equal(0,recovery.Dados.RestartPageIndex);Assert.False(recovery.Dados.RealProcessDurability);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await repo2.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(1,true),[]),default)).Situacao);
    }
    [Fact] public async Task CRASH_BEFORE_COMMIT_AND_READY_RECOVERY()
    {
        var h=new Harness();var t=await h.Ready();h.Storage.FalharProximaPromocao(h.C.Escopo.ScopeId,FalhaPromocaoFakeR24.BeforeCommit);await h.Promote(t);
        var recovered=await new CoordenadorSnapshotMasterdataR24(new SnapshotMasterdataMemoryFakeR24(h.Storage,new Clock(),ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(h.C.Escopo.ScopeId))).RecuperarAposReinicioAsync(new(h.C));
        Assert.Null(recovered.Dados!.Ativo);Assert.Contains(t.Id,recovered.Dados.AbandonedAttempts);
    }
    [Fact] public async Task RESTART_AFTER_COMMIT()
    {
        var h=new Harness();var promoted=(await h.Promote(await h.Ready())).Dados;
        var recovery=await new CoordenadorSnapshotMasterdataR24(new SnapshotMasterdataMemoryFakeR24(h.Storage,new Clock(),ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(h.C.Escopo.ScopeId))).RecuperarAposReinicioAsync(new(h.C));
        Assert.Equal(promoted,recovery.Dados!.Ativo);Assert.Empty(recovery.Dados.AbandonedAttempts);
    }
    [Theory][InlineData(CorrupcaoSnapshotFakeR24.Version)][InlineData(CorrupcaoSnapshotFakeR24.Fingerprint)][InlineData(CorrupcaoSnapshotFakeR24.Row)]
    public async Task VERSION_COMPATIBILITY_AND_CORRUPTION(CorrupcaoSnapshotFakeR24 damage)
    {
        var h=new Harness();await h.Promote(await h.Ready());await h.Promote(await h.Ready());h.Storage.CorromperAtivo(h.C.Escopo.ScopeId,damage);
        var r=await h.Read();Assert.Null(r.Dados);Assert.Equal("SEM_SNAPSHOT_COMPATIVEL",r.Codigo);
    }
    [Fact] public async Task CONCURRENT_PROMOTERS()
    {
        var h=new Harness();var a=await h.Ready();var b=await h.Ready();var second=new SnapshotMasterdataMemoryFakeR24(h.Storage,new Clock(),ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(h.C.Escopo.ScopeId));
        var results=await Task.WhenAll(Task.Run(()=>h.Promote(a)),Task.Run(()=>second.PromoverSnapshotAsync(new(h.C,b.Id,b.Base,b.Hash),default)));
        Assert.Single(results,r=>r.Situacao==SituacaoSnapshotR24.SUCCESS);Assert.Equal("CONFLICT_STALE_BASE",Assert.Single(results,r=>r.Situacao==SituacaoSnapshotR24.CONFLICT).Codigo);Assert.Equal(1,(await h.Read()).Dados!.Version);
    }
    [Fact] public async Task DUPLICATE_BP_REJECTED()
    {
        var h=new Harness();var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(0,false,Row(h.C)),[]),default);
        var r=await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(1,false,Row(h.C)),[]),default);Assert.Equal("DUPLICATE_BP",r.Codigo);Assert.Null((await h.Read()).Dados);
    }
    [Fact] public async Task SCOPE_MISMATCH_REJECTED()
    {
        var h=new Harness();var t=await h.Ready();var wrong=h.C with{Escopo=h.C.Escopo with{ScopeFingerprint=new string('0',64)}};
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Repo.PromoverSnapshotAsync(new(wrong,t.Id,t.Base,t.Hash),default)).Situacao);Assert.Null((await h.Read()).Dados);
    }
    [Fact] public async Task COMMIT_UNKNOWN_IDEMPOTENT_READBACK()
    {
        var h=new Harness();var t=await h.Ready();h.Storage.FalharProximaPromocao(h.C.Escopo.ScopeId,FalhaPromocaoFakeR24.AfterCommitUnknown);
        Assert.Equal(SituacaoSnapshotR24.INDETERMINATE,(await h.Promote(t)).Situacao);
        var other=new SnapshotMasterdataMemoryFakeR24(h.Storage,new Clock(),ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(h.C.Escopo.ScopeId));var resolved=await other.ResolverCommitIndeterminadoAsync(new(h.C,t.Id,t.Hash),default);
        Assert.Equal(SituacaoSnapshotR24.REPLAY,resolved.Situacao);var replay=await h.Promote(t);Assert.Equal(resolved.Dados!.SnapshotId,replay.Dados!.SnapshotId);Assert.Equal(1,(await h.Read()).Dados!.Version);
    }
    [Fact] public async Task COMMIT_NOT_OBSERVED_NEVER_RETRIES_AUTOMATICALLY()
    {
        var h=new Harness();var t=await h.Ready();h.Storage.FalharProximaPromocao(h.C.Escopo.ScopeId,FalhaPromocaoFakeR24.BeforeCommit);await h.Promote(t);
        var r=await h.Repo.ResolverCommitIndeterminadoAsync(new(h.C,t.Id,t.Hash),default);Assert.Null(r.Dados);Assert.Equal("COMMIT_NOT_OBSERVED",r.Codigo);Assert.Null((await h.Read()).Dados);
    }
    [Fact] public async Task READY_ATTEMPT_IMMUTABLE()
    {
        var h=new Harness();var t=await h.Ready();var r=await h.Repo.RegistrarLoteAsync(new(h.C,t.Id,ModoLoteSnapshotR24.APPEND_PAGE,Page(2,true),[]),default);
        Assert.Equal("ATTEMPT_IMMUTABLE",r.Codigo);Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await h.Promote(t)).Situacao);
    }
    [Fact] public async Task PROMOTED_SNAPSHOT_IMMUTABLE()
    {
        var h=new Harness();var t=await h.Ready();var original=(await h.Promote(t)).Dados!;
        var changed=original with{Rows=ImmutableArray<RegistroBpSnapshotR24>.Empty};Assert.Empty(changed.Rows);Assert.Single((await h.Read()).Dados!.Rows);
        var again=await h.Promote(t);Assert.Equal(SituacaoSnapshotR24.REPLAY,again.Situacao);Assert.Equal(original.SnapshotId,again.Dados!.SnapshotId);Assert.Equal(original.Version,again.Dados.Version);
    }
    [Fact] public async Task EMPTY_TERMINAL_PAGE(){var h=new Harness();var active=(await h.Promote(await h.Ready(empty:true))).Dados!;Assert.Empty(active.Rows);Assert.Equal(1,active.PageCount);}
    [Fact] public void FINGERPRINT_DETERMINISM()
    {
        var c=Context();var a=Row(c,"0002");var b=Row(c,"0001");Assert.Equal(SnapshotFingerprintR24.Snapshot(c,Now,2,[a,b]),SnapshotFingerprintR24.Snapshot(c,Now,2,[b,a]));
        Assert.NotEqual(SnapshotFingerprintR24.Row(c.Versoes,Row(c,name:null)),SnapshotFingerprintR24.Row(c.Versoes,Row(c,name:"")));
        Assert.NotEqual(SnapshotFingerprintR24.Row(c.Versoes,Row(c,"0001")),SnapshotFingerprintR24.Row(c.Versoes,Row(c,"1")));
    }
    [Fact] public async Task CANONICALIZATION_VERSION_MISMATCH()
    {
        var h=new Harness();var c=h.C with{Versoes=h.C.Versoes with{Normalizacao="OTHER_CANONICAL_VERSION"}};
        var r=await h.Repo.IniciarTentativaAsync(new(c,Guid.NewGuid(),Now),default);Assert.Equal("CANONICALIZATION_VERSION_MISMATCH",r.Codigo);
    }
    [Fact] public async Task ENRICHMENT_UPDATES_BEFORE_READY_ONLY()
    {
        var h=new Harness();var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(0,false,Row(h.C,enriched:false)),[]),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(1,true),[]),default);
        Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.VALIDATE_ENRICHMENT,null,[Row(h.C)]),default)).Situacao);
        var fp=SnapshotFingerprintR24.Snapshot(h.C,Now,2,[Row(h.C)]);Assert.Equal(EstadoTentativaSnapshotR24.READY,(await h.Repo.ValidarCompletudeAsync(new(h.C,id,1,fp),default)).Dados!.Estado);
    }
    [Fact] public async Task PAGE_GAPS_REJECTED()
    {
        var h=new Harness();var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(1,true),[]),default);
        var r=await h.Repo.ValidarCompletudeAsync(new(h.C,id,0,SnapshotFingerprintR24.Snapshot(h.C,Now,1,[])),default);Assert.Equal(SituacaoSnapshotR24.BLOCKED,r.Situacao);
    }
    [Fact] public async Task BAD_ROW_FINGERPRINT_REJECTED()
    {
        var h=new Harness();var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);
        var bad=Row(h.C) with{RowFingerprint=new string('0',64)};var r=await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(0,false,bad),[]),default);Assert.Equal(SituacaoSnapshotR24.BLOCKED,r.Situacao);
    }
    [Fact] public async Task WRONG_CAS_SNAPSHOT_ID_REJECTED()
    {
        var h=new Harness();var t=await h.Ready();var r=await h.Repo.PromoverSnapshotAsync(new(h.C,t.Id,new(Guid.NewGuid(),0),t.Hash),default);Assert.Equal("CONFLICT_STALE_BASE",r.Codigo);
    }
    [Fact] public async Task CANCELLED_ATTEMPT_NEVER_PROMOTED()
    {
        var h=new Harness();var t=await h.Ready();using var cts=new CancellationTokenSource();cts.Cancel();var r=await h.Repo.PromoverSnapshotAsync(new(h.C,t.Id,t.Base,t.Hash),cts.Token);
        Assert.Equal(SituacaoSnapshotR24.CANCELED,r.Situacao);Assert.Null((await h.Read()).Dados);
    }
    sealed class Pages(SapRoleRowR20A row):ISapRolePageReaderR20A {public Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A r,CancellationToken ct)=>Task.FromResult(new SapRolePageR20A(r.Skip==0?[row]:[]));}
    sealed class Bps:ISapBpMasterReaderR20B
    {public Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct)=>Task.FromResult(new SapBpReadResultR20B(bp,JsonSerializer.SerializeToElement(new{BusinessPartner=bp,BusinessPartnerFullName="OFFLINE_NAME",SecretTaxData="OFFLINE_TAX_DO_NOT_STORE",Email="offline@example.invalid"})));}
    sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>Now;}
    [Fact] public async Task SENSITIVE_FIELDS_NOT_STORED()
    {
        var role=new SapRoleRowR20A("0000000042","CRM010","/Date("+Now.AddDays(-30).ToUnixTimeMilliseconds()+")/","/Date("+Now.AddDays(30).ToUnixTimeMilliseconds()+")/");
        var roles=await new SapRolePagedEnumeratorR20A(new Pages(role),new Clock()).EnumerateAsync(new(10,10,100));
        var policy=new SapBpComparisonPolicyR20B([SapBpFieldR20B.BusinessPartner,SapBpFieldR20B.BusinessPartnerFullName],true,true,"OFFLINE_APPROVED",temporalContract:SapRoleTemporalContractR20B.EdmDateTimeOffset);
        var enriched=await new SapBpSnapshotEnricherR20B(new Bps(),new Clock()).EnrichAsync(roles,policy);
        var rows=SapSnapshotWhitelistProjectionR24.Project(enriched);var body=JsonSerializer.Serialize(rows);
        Assert.DoesNotContain("OFFLINE_TAX_DO_NOT_STORE",body);Assert.DoesNotContain("offline@example.invalid",body);Assert.Null(Assert.Single(rows).ApprovedFullNameNormalized);Assert.Equal("0000000042",rows[0].BusinessPartner);
        Assert.Throws<InvalidOperationException>(()=>SapSnapshotWhitelistProjectionR24.Project(enriched,true,"OFFLINE_RETENTION_APPROVED",ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(Guid.NewGuid())));
    }
    [Fact] public async Task NO_FAKE_SAP_WATERMARK()
    {
        var h=new Harness();var s=(await h.Promote(await h.Ready())).Dados!;Assert.Equal(Now,s.ReferenceAt);Assert.False(s.SapTransactionalConsistencyProven);Assert.False(s.CorporateCompletenessProven);
        Assert.DoesNotContain(s.GetType().GetProperties(),p=>p.Name.Contains("Watermark",StringComparison.OrdinalIgnoreCase));
    }
    [Fact] public async Task NO_GUARDIAN_SIDE_EFFECT(){var h=new Harness();Assert.False((await h.Promote(await h.Ready())).Dados!.GuardianWriteAuthorized);}
    [Fact] public async Task NO_RUNTIME_AUTO_ACTIVATION(){var h=new Harness();var c=new CoordenadorSnapshotMasterdataR24(h.Repo);Assert.False(c.RuntimeActivation);Assert.Null((await h.Read()).Dados);}
    sealed class ManualSap:ISapCarrierManualReadOnly{public Task<CarrierManualSap> ConsultarBpAsync(string bp,CancellationToken ct)=>Task.FromResult(new CarrierManualSap(bp,new Dictionary<string,string?>(),[new("CRM010",Now.AddDays(-30),Now.AddDays(30))]));}
    sealed class ManualGuardian:IGuardianCarrierManualReadOnly{public Task<CarrierManualGuardian> ConsultarCodigoAsync(CarrierManualIdentity id,CancellationToken ct)=>Task.FromResult(new CarrierManualGuardian(CarrierLookupState.Absent,null,new Dictionary<string,string?>(),true));}
    [Fact] public async Task R15_MANUAL_PRESERVED()
    {
        var useCase=new ConsultarTransportadoraManualReadOnlyCasoDeUso(new ManualSap(),new ManualGuardian(),_=>true,_=>new("OFFLINE_CODE","OFFLINE_SIGNATURE",true,"OFFLINE_PROOF"),new([],false,"OFFLINE_SCOPE"),new Clock());
        Assert.Equal(DecisaoCarrierManual.CREATE_CANDIDATE,(await useCase.ExecutarAsync("0001",default)).Decisao);
        var h=new Harness();await h.Promote(await h.Ready());Assert.Equal(DecisaoCarrierManual.CREATE_CANDIDATE,(await useCase.ExecutarAsync("0001",default)).Decisao);
    }
    [Fact] public async Task CANCEL_AT_COMMIT_BOUNDARY_IS_SAFE_TYPED_RESULT()
    {
        var h=new Harness();await h.Promote(await h.Ready());var old=(await h.Read()).Dados;var t=await h.Ready();using var cts=new CancellationTokenSource();
        h.Storage.AntesDoProximoCommitDePromocao(h.C.Escopo.ScopeId,()=>cts.Cancel());
        var r=await h.Repo.PromoverSnapshotAsync(new(h.C,t.Id,t.Base,t.Hash),cts.Token);
        Assert.Equal(SituacaoSnapshotR24.CANCELED,r.Situacao);Assert.Equal(old,(await h.Read()).Dados);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Promote(t)).Situacao);
    }
    [Fact] public async Task ATOMIC_OBSERVER_SEES_ONLY_PREVIOUS_UNTIL_COMMIT()
    {
        var h=new Harness();await h.Promote(await h.Ready());var old=(await h.Read()).Dados;var t=await h.Ready();SnapshotConsolidadoR24? during=null;
        h.Storage.AntesDoProximoCommitDePromocao(h.C.Escopo.ScopeId,()=>during=new SnapshotMasterdataMemoryFakeR24(h.Storage,new Clock(),ContextoTemporalSinteticoSnapshotR25D.ForFixtureScope(h.C.Escopo.ScopeId)).CarregarSnapshotAtivoAsync(new(h.C),default).GetAwaiter().GetResult().Dados);
        var after=(await h.Promote(t)).Dados;Assert.Equal(old,during);Assert.NotEqual(old!.SnapshotId,after!.SnapshotId);
    }
    [Fact] public async Task ACTIVE_READ_UNSUPPORTED_VERSION_HAS_NO_COMPATIBLE_SNAPSHOT()
    {
        var h=new Harness();await h.Promote(await h.Ready());var wrong=h.C with{Versoes=h.C.Versoes with{Contrato=2}};
        var r=await h.Repo.CarregarSnapshotAtivoAsync(new(wrong),default);Assert.Null(r.Dados);Assert.Equal("SEM_SNAPSHOT_COMPATIVEL",r.Codigo);
    }
    [Theory]
    [InlineData("0001",null,"d62019bca2077f85b617bdb8e71456f7b0b71031f3c0b21190d4329aaaaaedf9")]
    [InlineData("0001","","d8dd7ab71067e263248394072bebb2cfea20703e4b61134ffb2eba8222522d37")]
    [InlineData("0002","OFFLINE_VECTOR","09c53bee66fadedd5076f2236c4ea0d2e9cff993f698defd36b874c9fe590d9e")]
    public void DETERMINISTIC_BINARY_VECTORS(string bp,string? name,string expected)
    {var c=Context();Assert.Equal(expected,SnapshotFingerprintR24.Row(c.Versoes,Row(c,bp,name:name)));}
    [Fact] public async Task PENDING_ENRICHMENT_NEVER_READY()
    {
        var h=new Harness();var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);var row=Row(h.C,enriched:false);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(0,false,row),[]),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(1,true),[]),default);
        var r=await h.Repo.ValidarCompletudeAsync(new(h.C,id,1,SnapshotFingerprintR24.Snapshot(h.C,Now,2,[row])),default);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,r.Situacao);Assert.Equal(EstadoTentativaSnapshotR24.REJECTED,r.Dados!.Estado);
    }
    [Fact] public async Task DECLARED_PAGE_COUNT_MISMATCH_REJECTED()
    {
        var h=new Harness();var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);var page=Page(0,false,Row(h.C)) with{DeclaredRowCount=2};
        var r=await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,page,[]),default);Assert.Equal("PAGE_SHAPE_INVALID",r.Codigo);
    }
    [Fact] public async Task ABANDON_PROMOTED_ATTEMPT_REJECTED()
    {
        var h=new Harness();var t=await h.Ready();var old=(await h.Promote(t)).Dados;
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Repo.AbandonarTentativaAsync(new(h.C,t.Id),default)).Situacao);Assert.Equal(old,(await h.Read()).Dados);
    }
    [Fact] public async Task READBACK_OF_HISTORICAL_COMMIT_IS_EXPLICIT()
    {
        var h=new Harness();var first=await h.Ready();var prior=(await h.Promote(first)).Dados;await h.Promote(await h.Ready());
        var resolved=await h.Repo.ResolverCommitIndeterminadoAsync(new(h.C,first.Id,first.Hash),default);
        Assert.Equal(prior!.SnapshotId,resolved.Dados!.SnapshotId);Assert.Equal("COMMIT_CONFIRMED_HISTORICAL",resolved.Codigo);Assert.Equal(2,(await h.Read()).Dados!.Version);
    }

    [Fact] public async Task CROSS_PAGE_ORDERING_CONTRACT_REJECTED()
    {
        var h=new Harness();var id=Guid.NewGuid();await h.Repo.IniciarTentativaAsync(new(h.C,id,Now),default);
        var a=Row(h.C,"0002");var b=Row(h.C,"0001");
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(0,false,a),[]),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(1,false,b),[]),default);
        await h.Repo.RegistrarLoteAsync(new(h.C,id,ModoLoteSnapshotR24.APPEND_PAGE,Page(2,true),[]),default);
        var r=await h.Repo.ValidarCompletudeAsync(new(h.C,id,2,SnapshotFingerprintR24.Snapshot(h.C,Now,3,[a,b])),default);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,r.Situacao);Assert.Null((await h.Read()).Dados);
    }

}
