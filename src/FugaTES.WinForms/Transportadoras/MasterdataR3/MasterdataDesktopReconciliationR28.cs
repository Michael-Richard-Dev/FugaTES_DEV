using System.Collections.Immutable;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

// A read-only binding, not a scheduler or a durable store. No Guardian gateway or DML delegate.
public sealed class MasterdataDesktopReconciliationR28
{
    readonly Func<bool> activation;
    public MasterdataPagedReconciliationCoordinatorR20C Coordinator {get;}
    public ImmutableArray<RegistroBpSnapshotR24> Observation {get;private set;}=[];
    public ImmutableArray<string> EligibleBusinessPartners=>Observation.Where(r=>r.Crm010TemporalEvaluation==AvaliacaoCrm010R24.EVALUATED_VALID).Select(r=>r.BusinessPartner).ToImmutableArray();
    public bool CanActivate {get{try{return activation()&&Coordinator.ReadAuthorizationEffective;}catch{return false;}}}
    public const string Blocker="MASTERDATA_POSTGRESQL_ADAPTER_NOT_APPROVED";
    public MasterdataDesktopReconciliationR28(OrquestradorCompartilhadoMasterdataR3 shared,
        ISapRolePageReaderR20A pages,ISapBpMasterReaderR20B bps,SapBpComparisonPolicyR20B policy,
        MasterdataReconciliationCycleLimitsR20C limits,Func<MasterdataSapReadAuthorizationR20C?> authorization,
        Func<bool> runtimeAndPersistenceApproved,TimeProvider clock)
    {
        if(policy.TemporalContract!=SapRoleTemporalContractR20B.R25TBusinessDate)
            throw new ArgumentException("MASTERDATA_R25T_POLICY_REQUIRED");
        activation=runtimeAndPersistenceApproved;
        Coordinator=new(shared,pages,bps,policy,limits,()=>activation()?authorization():null,clock);
    }
    public async Task<MasterdataReconciliationCycleResultR20C> ExecuteAsync(CancellationToken ct)
    {
        if(!CanActivate)
        {
            // Mark the old partial scan for restart even when the shared lock is busy.
            using var revoked=new CancellationTokenSource();revoked.Cancel();
            await Coordinator.ExecuteNextCycleAsync(revoked.Token).ConfigureAwait(false);
            return new(MasterdataReconciliationCycleStateR20C.Blocked,Blocker,null,0,0,0,0,[]);
        }
        var result=await Coordinator.ExecuteNextCycleAsync(ct).ConfigureAwait(false);
        // In-memory observation only; R20C already validated completeness and R25T under the shared lock.
        // No R24 promotion, fake persistence or operational checkpoint is substituted for PostgreSQL.
        if(result.State==MasterdataReconciliationCycleStateR20C.Promoted&&Coordinator.ConsolidatedSnapshot is {} snapshot)
            Observation=SapSnapshotWhitelistProjectionR24.Project(snapshot);
        return result;
    }
    public static MasterdataDesktopReconciliationR28 Blocked(OrquestradorCompartilhadoMasterdataR3 shared,TimeProvider clock)
        =>new(shared,new UnavailableSap(),new UnavailableSap(),
            new([SapBpFieldR20B.BusinessPartner],true,true,"R28_OBSERVATION_ONLY_NOT_GUARDIAN_EQUIVALENCE",temporalContract:SapRoleTemporalContractR20B.R25TBusinessDate),
            new(1,1,new(100,10000,1000000)),()=>null,()=>false,clock);
    sealed class UnavailableSap:ISapRolePageReaderR20A,ISapBpMasterReaderR20B
    {
        public Task<SapRolePageR20A> ReadAsync(SapRolePageRequestR20A r,CancellationToken ct)=>throw new InvalidOperationException("MASTERDATA_SAP_PROFILE_NOT_APPROVED");
        public Task<SapBpReadResultR20B> ReadAsync(string bp,CancellationToken ct)=>throw new InvalidOperationException("MASTERDATA_SAP_PROFILE_NOT_APPROVED");
    }
}
