using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
using FugaTES.Persistencia.Transportadoras.Fakes;
using FugaTES.Integracao.SAP.BusinessPartner;
namespace FugaTES.DurableSnapshot.R24.Tests;
public class PrivacyAndTemporalR25TTests
{
    static DateTimeOffset D(string s)=>DateTimeOffset.Parse(s,CultureInfo.InvariantCulture);
    static readonly DateTimeOffset Now=D("2026-10-11T12:00:00Z");
    static readonly DateTimeOffset From=D("2026-10-10T00:00:00-03:00");
    static readonly DateTimeOffset To=D("2026-10-12T00:00:00-03:00");
    sealed class Clock:TimeProvider {public override DateTimeOffset GetUtcNow()=>Now.AddHours(1);}
    sealed class Harness
    {
        public readonly ContextoSnapshotR24 C;public readonly SnapshotMemoryStorageR24 Storage=new();public readonly SnapshotMasterdataMemoryFakeR24 Repo;
        public Guid Id;public string Hash="";
        public Harness()
        {
            var s=new EscopoSnapshotR24(Guid.NewGuid(),"SAP_HML","110","R20A_V1","");
            C=new(s with{ScopeFingerprint=SnapshotFingerprintR24.Scope(s)},VersoesSnapshotR24.AprovadaR25T);Repo=new(Storage,new Clock());
        }
        public RegistroBpSnapshotR24 Row(string? name=null,bool enriched=true)
        {
            var r=new RegistroBpSnapshotR24("0001",AvaliacaoCrm010R24.EVALUATED_VALID,From,To,name,enriched);
            return r with{RowFingerprint=SnapshotFingerprintR24.Row(C.Versoes,r)};
        }
        public PaginaSnapshotR24 Page(RegistroBpSnapshotR24 r)
        {var p=new PaginaSnapshotR24(0,0,1,1,true,[r],"");return p with{PageFingerprint=SnapshotFingerprintR24.Page(p)};}
        public async Task Start(){Id=Guid.NewGuid();await Repo.IniciarTentativaAsync(new(C,Id,Now),default);}
        public Task<ResultadoSnapshotR24<TentativaSnapshotR24>> Append(PaginaSnapshotR24 p)=>Repo.RegistrarLoteAsync(new(C,Id,ModoLoteSnapshotR24.APPEND_PAGE,p,[]),default);
        public Task<ResultadoSnapshotR24<TentativaSnapshotR24>> Validate()
        {Hash=SnapshotFingerprintR24.Snapshot(C,Now,1,[Row()]);return Repo.ValidarCompletudeAsync(new(C,Id,1,Hash),default);}
        public Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> Promote(PonteiroSnapshotR24? baseline=null)=>Repo.PromoverSnapshotAsync(new(C,Id,baseline??PonteiroSnapshotR24.Vazio,Hash),default);
        public Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> Read()=>Repo.CarregarSnapshotAtivoAsync(new(C),default);
        public async Task<SnapshotConsolidadoR24> Good()
        {await Start();Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await Append(Page(Row()))).Situacao);Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await Validate()).Situacao);return (await Promote()).Dados!;}
    }
    [Fact] public async Task DIRECT_STORE_NONNULL_NAME_REJECTED()
    {var h=new Harness();await h.Start();var r=await h.Append(h.Page(h.Row("OFFLINE_RESTRICTED_NAME")));Assert.Equal(SituacaoSnapshotR24.BLOCKED,r.Situacao);Assert.Equal("FULL_NAME_RETENTION_NOT_APPROVED",r.Codigo);}
    [Fact] public async Task VALID_FINGERPRINT_DOES_NOT_AUTHORIZE_NAME()
    {
        var h=new Harness();await h.Start();var r=h.Row("OFFLINE_RESTRICTED_NAME");Assert.Equal(SnapshotFingerprintR24.Row(h.C.Versoes,r),r.RowFingerprint);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Append(h.Page(r))).Situacao);
        var changed=r with{ApprovedFullNameNormalized="OFFLINE_ANOTHER_NAME"};changed=changed with{RowFingerprint=SnapshotFingerprintR24.Row(h.C.Versoes,changed)};
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Append(h.Page(changed))).Situacao);
    }
    [Fact] public async Task UNAPPROVED_RETENTION_REFERENCE_REJECTED()
    {
        var h=new Harness();await h.Start();var r=h.Row("OFFLINE_RETENTION_APPROVED");
        Assert.Equal("FULL_NAME_RETENTION_NOT_APPROVED",(await h.Append(h.Page(r))).Codigo);
    }
    [Fact] public async Task NULL_NAME_ACCEPTED_WHEN_OTHER_RULES_VALID()
    {var h=new Harness();var s=await h.Good();Assert.Null(Assert.Single(s.Rows).ApprovedFullNameNormalized);Assert.Equal(VersoesSnapshotR24.AprovadaR25T,s.Versoes);}
    [Fact] public async Task REJECTED_ROW_NOT_PROMOTED()
    {var h=new Harness();await h.Start();await h.Append(h.Page(h.Row("OFFLINE_RESTRICTED_NAME")));Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Validate()).Situacao);Assert.Null((await h.Promote()).Dados);Assert.Null((await h.Read()).Dados);}
    [Fact] public async Task PREVIOUS_SNAPSHOT_PRESERVED()
    {
        var h=new Harness();var prior=await h.Good();await h.Start();await h.Append(h.Page(h.Row("OFFLINE_RESTRICTED_NAME")));await h.Validate();
        Assert.Null((await h.Promote(new(prior.SnapshotId,prior.Version))).Dados);Assert.Equal(prior,(await h.Read()).Dados);
    }
    [Fact] public async Task NO_NAME_EXPOSURE_IN_ERRORS()
    {
        var h=new Harness();await h.Start();var r=await h.Append(h.Page(h.Row("OFFLINE_RESTRICTED_NAME")));
        Assert.DoesNotContain("OFFLINE_RESTRICTED_NAME",JsonSerializer.Serialize(r));Assert.Equal("FULL_NAME_RETENTION_NOT_APPROVED",r.Codigo);
    }
    [Fact] public async Task CALLER_MUTATION_CANNOT_BYPASS_GUARD()
    {
        var h=new Harness();await h.Start();var external=new[]{h.Row()};
        var p=new PaginaSnapshotR24(0,0,1,1,true,ImmutableCollectionsMarshal.AsImmutableArray(external),"");p=p with{PageFingerprint=SnapshotFingerprintR24.Page(p)};
        Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await h.Append(p)).Situacao);
        external[0]=h.Row("OFFLINE_RESTRICTED_NAME");
        Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await h.Validate()).Situacao);var s=(await h.Promote()).Dados!;
        Assert.Null(Assert.Single(s.Rows).ApprovedFullNameNormalized);Assert.Null(Assert.Single((await h.Read()).Dados!.Rows).ApprovedFullNameNormalized);
    }
    [Fact] public async Task REPLAY_CANNOT_INJECT_RESTRICTED_NAME()
    {
        var h=new Harness();var s=await h.Good();Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Append(h.Page(h.Row("OFFLINE_RESTRICTED_NAME")))).Situacao);
        var replay=await h.Promote();Assert.Equal(SituacaoSnapshotR24.REPLAY,replay.Situacao);Assert.Equal(s,replay.Dados);Assert.Null(replay.Dados!.Rows[0].ApprovedFullNameNormalized);
    }
    [Fact] public async Task ENRICHMENT_NONNULL_NAME_REJECTED()
    {
        var h=new Harness();await h.Start();await h.Append(h.Page(h.Row(enriched:false)));
        var r=await h.Repo.RegistrarLoteAsync(new(h.C,h.Id,ModoLoteSnapshotR24.VALIDATE_ENRICHMENT,null,[h.Row("OFFLINE_RESTRICTED_NAME")]),default);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,r.Situacao);Assert.Equal("FULL_NAME_RETENTION_NOT_APPROVED",r.Codigo);Assert.Null((await h.Read()).Dados);
    }
    [Fact] public async Task RECOVERY_PRESERVES_NULL_NAME()
    {
        var h=new Harness();var s=await h.Good();var replica=new SnapshotMasterdataMemoryFakeR24(h.Storage,new Clock());
        var r=await replica.RecuperarAposReinicioAsync(new(h.C),default);Assert.Equal(s,r.Dados!.Ativo);Assert.Null(r.Dados.Ativo!.Rows[0].ApprovedFullNameNormalized);
    }
    static ResultadoTemporalCrm010R25T Eval(DateTimeOffset reference,DateTimeOffset? from=null,DateTimeOffset? to=null)=>new Crm010BusinessDatePolicyR25T().Evaluate(from??From,to??To,reference);
    [Theory][InlineData(9,false)][InlineData(10,true)][InlineData(11,true)][InlineData(12,false)][InlineData(13,false)]
    public void CRM010_NORMATIVE_MIDNIGHT_EXAMPLE(int day,bool valid)
    {Assert.Equal(valid?AvaliacaoCrm010R24.EVALUATED_VALID:AvaliacaoCrm010R24.EVALUATED_INVALID,Eval(D($"2026-10-{day:00}T12:00:00-03:00")).Status);}
    [Fact] public void CRM010_START_DATE_INCLUSIVE()=>Assert.Equal(AvaliacaoCrm010R24.EVALUATED_VALID,Eval(From).Status);
    [Fact] public void CRM010_END_DATE_INCLUSIVE()=>Assert.Equal(AvaliacaoCrm010R24.EVALUATED_VALID,Eval(D("2026-10-12T23:59:59-03:00"),to:D("2026-10-12T15:00:00-03:00")).Status);
    [Fact] public void CRM010_END_AT_EXACT_MIDNIGHT_EXCLUDES_DAY()=>Assert.Equal(AvaliacaoCrm010R24.EVALUATED_INVALID,Eval(To).Status);
    [Fact] public void CRM010_END_AT_235959_INCLUDES_DAY()=>Assert.Equal(AvaliacaoCrm010R24.EVALUATED_VALID,Eval(D("2026-10-12T23:59:59.9999999-03:00"),to:D("2026-10-12T23:59:59-03:00")).Status);
    [Fact] public void CRM010_MIDNIGHT_UTC_OFFSET_EQUIVALENCE()
    {Assert.Equal(Eval(Now),Eval(Now,to:D("2026-10-12T03:00:00Z")));Assert.Equal(new DateOnly(2026,10,11),Eval(Now,to:D("2026-10-12T03:00:00Z")).EffectiveEndDate);}
    [Fact] public void CRM010_END_ONE_TICK_AFTER_MIDNIGHT()=>Assert.Equal(AvaliacaoCrm010R24.EVALUATED_VALID,Eval(To,to:To.AddTicks(1)).Status);
    [Fact] public async Task CRM010_REFERENCE_DATE_FIXED_PER_SCAN()
    {var h=new Harness();var s=await h.Good();Assert.Equal(Now,s.ReferenceAt);Assert.Equal(new DateOnly(2026,10,11),Eval(s.ReferenceAt).ReferenceDate);Assert.NotEqual(s.ReferenceAt,s.ConsolidatedAt);}
    [Fact] public void CRM010_MISSING_VALIDFROM_INDETERMINATE()=>Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,new Crm010BusinessDatePolicyR25T().Evaluate(null,To,Now).Status);
    [Fact] public void CRM010_MISSING_VALIDTO_INDETERMINATE()=>Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,new Crm010BusinessDatePolicyR25T().Evaluate(From,null,Now).Status);
    [Fact] public void CRM010_REVERSED_RANGE_INDETERMINATE()
    {Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,new Crm010BusinessDatePolicyR25T().Evaluate(To,From,Now).Status);Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,new Crm010BusinessDatePolicyR25T().Evaluate(From,From,Now).Status);}
    static string OData(DateTimeOffset d,string suffix="")=>"/Date("+d.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)+suffix+")/";
    [Fact] public void CRM010_INVALID_ODATA_INDETERMINATE()
    {Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,SapSnapshotWhitelistProjectionR24.EvaluateApprovedTemporal("invalid",OData(To),Now).Status);Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,SapSnapshotWhitelistProjectionR24.EvaluateApprovedTemporal(OData(From),"/Date(0+1460)/",Now).Status);}
    [Fact] public void CRM010_ODATA_V2_OFFSET_PRESERVED()
    {
        var from=CarrierManualODataDateParser.Parse(OData(From,"-0300"));Assert.Equal(From,from);Assert.Equal(TimeSpan.FromHours(-3),from!.Value.Offset);
        Assert.Equal(Eval(Now),SapSnapshotWhitelistProjectionR24.EvaluateApprovedTemporal(OData(From,"-0300"),OData(To,"+0000"),Now));
    }
    [Fact] public void CRM010_TIMEZONE_UNAVAILABLE_FAIL_CLOSED()
    {var policy=new Crm010BusinessDatePolicyR25T(id=>{Assert.Equal("America/Sao_Paulo",id);return null;});Assert.Equal(AvaliacaoCrm010R24.INDETERMINATE,policy.Evaluate(From,To,Now).Status);}
    [Fact] public void CRM010_HISTORICAL_TIMEZONE_NOT_FIXED_OFFSET()
    {
        // In Nov 2018 Sao Paulo used daylight saving: 02:00Z is local midnight.
        var result=new Crm010BusinessDatePolicyR25T().Evaluate(D("2018-11-19T02:00:00Z"),D("2018-11-21T02:00:00Z"),D("2018-11-20T02:00:00Z"));
        Assert.Equal(new DateOnly(2018,11,20),result.EffectiveEndDate);Assert.Equal(AvaliacaoCrm010R24.EVALUATED_VALID,result.Status);
    }
    [Fact] public async Task CRM010_OLD_POLICY_NOT_SILENTLY_REUSED()
    {
        var h=new Harness();await h.Good();var old=h.C with{Versoes=VersoesSnapshotR24.PropostaAtual};
        Assert.Null((await h.Repo.CarregarSnapshotAtivoAsync(new(old),default)).Dados);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Repo.RecuperarAposReinicioAsync(new(old),default)).Situacao);
    }
    [Fact] public async Task CRM010_SNAPSHOT_VERSION_MISMATCH_BLOCKED()
    {
        var h=new Harness();await h.Good();var mismatch=h.C with{Versoes=h.C.Versoes with{Comparacao="UNKNOWN_TEMPORAL_POLICY"}};
        Assert.Null((await h.Repo.CarregarSnapshotAtivoAsync(new(mismatch),default)).Dados);
    }
    [Fact] public async Task CRM010_INDETERMINATE_NOT_READY()
    {
        var h=new Harness();await h.Start();var r=h.Row() with{RoleValidToObserved=null,Crm010TemporalEvaluation=AvaliacaoCrm010R24.INDETERMINATE};r=r with{RowFingerprint=SnapshotFingerprintR24.Row(h.C.Versoes,r)};
        await h.Append(h.Page(r));var fp=SnapshotFingerprintR24.Snapshot(h.C,Now,1,[r]);
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Repo.ValidarCompletudeAsync(new(h.C,h.Id,1,fp),default)).Situacao);Assert.Null((await h.Read()).Dados);
    }
    [Fact] public async Task CRM010_NO_GUARDIAN_WRITE()
    {var h=new Harness();var s=await h.Good();Assert.False(s.GuardianWriteAuthorized);Assert.False(new CoordenadorSnapshotMasterdataR24(h.Repo).RuntimeActivation);}
    [Fact] public async Task CRM010_WRONG_VERSION_RECOVERY_DOES_NOT_ABANDON_APPROVED_ATTEMPT()
    {
        var h=new Harness();var active=await h.Good();await h.Start();await h.Append(h.Page(h.Row()));await h.Validate();
        var old=h.C with{Versoes=VersoesSnapshotR24.PropostaAtual};
        Assert.Equal(SituacaoSnapshotR24.BLOCKED,(await h.Repo.RecuperarAposReinicioAsync(new(old),default)).Situacao);
        Assert.Equal(SituacaoSnapshotR24.SUCCESS,(await h.Promote(new(active.SnapshotId,active.Version))).Situacao);
    }
}
