using System.Collections.Immutable;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
namespace FugaTES.Integracao.SAP.BusinessPartner;

public static class SapSnapshotWhitelistProjectionR24
{
    public static ResultadoTemporalCrm010R25T EvaluateApprovedTemporal(string? validFrom,string? validTo,DateTimeOffset referenceAt,
        Crm010BusinessDatePolicyR25T? policy=null)=>
        (policy??new()).Evaluate(CarrierManualODataDateParser.Parse(validFrom),CarrierManualODataDateParser.Parse(validTo),referenceAt);

    public static ImmutableArray<RegistroBpSnapshotR24> Project(SapBpEnrichedSnapshotR20B snapshot,
        bool retainApprovedFullName=false,string? retentionApprovalReference=null,
        ContextoTemporalSinteticoSnapshotR25D? syntheticContext=null,VersoesSnapshotR24? versions=null)
    {
        if(!snapshot.Complete||!snapshot.Valid)throw new InvalidOperationException("R20B_SNAPSHOT_NOT_VALID");
        var effectiveVersions=versions??VersoesSnapshotR24.AprovadaR25T;
        if(!SnapshotFingerprintR24.Supported(effectiveVersions))throw new InvalidOperationException("VERSION_INCOMPATIBLE");
        if(retainApprovedFullName)
            throw new InvalidOperationException("FULL_NAME_RETENTION_NOT_APPROVED");
        var roles=snapshot.Roles.Rows.ToDictionary(x=>x.BusinessPartner,StringComparer.Ordinal);
        var rows=ImmutableArray.CreateBuilder<RegistroBpSnapshotR24>();
        foreach(var entry in snapshot.Entries.OrderBy(x=>x.BusinessPartner,StringComparer.Ordinal))
        {
            var role=roles[entry.BusinessPartner];
            var from=CarrierManualODataDateParser.Parse(role.ValidFrom);var to=CarrierManualODataDateParser.Parse(role.ValidTo);
            var temporal=AvaliacaoCrm010R24.INDETERMINATE;
            // R25T has explicit functional approval; the legacy proposal requires a QA-only capability.
            if(effectiveVersions==VersoesSnapshotR24.AprovadaR25T)temporal=new Crm010BusinessDatePolicyR25T().Evaluate(from,to,snapshot.ReferenceAt).Status;
            else if(syntheticContext is not null)temporal=syntheticContext.Evaluate(entry.BusinessPartner,from,to,snapshot.ReferenceAt);
            string? name=null;
            var row=new RegistroBpSnapshotR24(entry.BusinessPartner,temporal,from,to,name,
                entry.ComparisonStatus==SapBpComparisonStatusR20B.Complete);
            rows.Add(row with{RowFingerprint=SnapshotFingerprintR24.Row(effectiveVersions,row)});
        }
        // Deliberately never read/copy RAW_SAP_DATA, tax/address/contact or Guardian identity.
        return rows.ToImmutable();
    }
}
