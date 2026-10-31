using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("FugaTES.DurableSnapshot.R24.Tests")]
namespace FugaTES.Aplicacao.Transportadoras.SnapshotsR24;

// No public constructor/factory or configuration flag. Only the offline QA friend assembly
// can create this capability. It does not represent approval of real CRM010 semantics.
public sealed class ContextoTemporalSinteticoSnapshotR25D
{
    private readonly Guid scopeId;
    private ContextoTemporalSinteticoSnapshotR25D(Guid scopeId)=>this.scopeId=scopeId;
    internal static ContextoTemporalSinteticoSnapshotR25D ForFixtureScope(Guid scopeId)=>new(scopeId);
    public bool MatchesScope(Guid scope)=>scopeId==scope&&scope!=Guid.Empty;
    public bool AllowsFixture(string bp,string? name=null)=>
        bp is "0001" or "0002" or "1" or "0000000042" &&
        (name is null or "" || name.StartsWith("OFFLINE_",StringComparison.Ordinal));
    public AvaliacaoCrm010R24 Evaluate(string bp,DateTimeOffset? from,DateTimeOffset? to,DateTimeOffset reference)
    {
        if(!AllowsFixture(bp)||from is null||to is null||from>=to||reference==from||reference==to)
            return AvaliacaoCrm010R24.INDETERMINATE;
        return reference>from&&reference<to?AvaliacaoCrm010R24.EVALUATED_VALID:AvaliacaoCrm010R24.EVALUATED_INVALID;
    }
}
