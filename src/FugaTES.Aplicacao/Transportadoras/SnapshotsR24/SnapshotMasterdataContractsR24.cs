using System.Collections.Immutable;
namespace FugaTES.Aplicacao.Transportadoras.SnapshotsR24;

public enum EstadoTentativaSnapshotR24 { SCANNING, ENRICHING, READY, PROMOTED, REJECTED, ABANDONED }
public enum AvaliacaoCrm010R24 { INDETERMINATE, EVALUATED_VALID, EVALUATED_INVALID }
public enum SituacaoSnapshotR24 { SUCCESS, REPLAY, BLOCKED, CONFLICT, CANCELED, INDETERMINATE, NOT_FOUND }
public enum ModoLoteSnapshotR24 { APPEND_PAGE, VALIDATE_ENRICHMENT }
public sealed record VersoesSnapshotR24(int Contrato, string Normalizacao, string Comparacao)
{
    public static VersoesSnapshotR24 AprovadaR25T { get; } = new(2,"R24_BINARY_PROPOSAL_V1",Crm010BusinessDatePolicyR25T.PolicyVersion);
    public static VersoesSnapshotR24 PropostaAtual { get; } = new(1,"R24_BINARY_PROPOSAL_V1","R24_EXACT_INSTANT_PROPOSAL_V1");
}
public sealed record EscopoSnapshotR24(Guid ScopeId,string SapSourceCode,string SapClientCode,string QueryContractVersion,string ScopeFingerprint)
{
    public string SapEntity => "A_BusinessPartnerRole";
    public string CrmRoleCode => "CRM010";
    public string QueryFilterContract => "CRM010_CENTER_DISABLED";
    public string QueryOrderContract => "BUSINESS_PARTNER_ASC";
    public string PagingContract => "TOP_SKIP";
}
public sealed record ContextoSnapshotR24(EscopoSnapshotR24 Escopo,VersoesSnapshotR24 Versoes);
// Strict R23 observation whitelist. No dictionaries, JSON, tax/address/contact or Guardian fields.
public sealed record RegistroBpSnapshotR24(string BusinessPartner,
    AvaliacaoCrm010R24 Crm010TemporalEvaluation= AvaliacaoCrm010R24.INDETERMINATE,
    DateTimeOffset? RoleValidFromObserved=null,DateTimeOffset? RoleValidToObserved=null,
    string? ApprovedFullNameNormalized=null,bool EnrichmentValidated=false,string RowFingerprint="");
public sealed record PaginaSnapshotR24(int PageIndex,long ObservedOffset,int RequestedTop,int DeclaredRowCount,
    bool TerminalPage,ImmutableArray<RegistroBpSnapshotR24> Rows,string PageFingerprint);
public sealed record PonteiroSnapshotR24(Guid? SnapshotId,long ActiveVersion)
{
    public static PonteiroSnapshotR24 Vazio { get; } = new(null,0);
}
public sealed record TentativaSnapshotR24(Guid AttemptId,Guid ScopeId,EstadoTentativaSnapshotR24 Estado,
    int PageCount,int BpCount,DateTimeOffset ReferenceAt,string? SnapshotFingerprint,PonteiroSnapshotR24 Base);
public sealed record SnapshotConsolidadoR24(Guid SnapshotId,Guid ScopeId,Guid AttemptId,long Version,
    string ScopeFingerprint,VersoesSnapshotR24 Versoes,DateTimeOffset ReferenceAt,int PageCount,int BpCount,
    ImmutableArray<RegistroBpSnapshotR24> Rows,string SnapshotFingerprint,
    DateTimeOffset ValidatedAt,DateTimeOffset ConsolidatedAt)
{
    public bool SapTransactionalConsistencyProven => false;
    public bool CorporateCompletenessProven => false;
    public bool GuardianWriteAuthorized => false;
}
public sealed record ResultadoSnapshotR24<T>(SituacaoSnapshotR24 Situacao,string Codigo,Guid ScopeId,Guid? AttemptId,T? Dados);
public sealed record IniciarTentativaSnapshotR24(ContextoSnapshotR24 Contexto,Guid AttemptId,DateTimeOffset ReferenceAt);
public sealed record RegistrarLoteSnapshotR24(ContextoSnapshotR24 Contexto,Guid AttemptId,ModoLoteSnapshotR24 Modo,
    PaginaSnapshotR24? Pagina,ImmutableArray<RegistroBpSnapshotR24> EnrichmentRows);
public sealed record ValidarCompletudeSnapshotR24(ContextoSnapshotR24 Contexto,Guid AttemptId,int ExpectedBpCount,string ExpectedSnapshotFingerprint);
public sealed record PromoverSnapshotR24(ContextoSnapshotR24 Contexto,Guid AttemptId,PonteiroSnapshotR24 ExpectedBase,string ExpectedSnapshotFingerprint);
public sealed record CarregarSnapshotAtivoR24(ContextoSnapshotR24 Contexto);
public sealed record AbandonarTentativaSnapshotR24(ContextoSnapshotR24 Contexto,Guid AttemptId);
public sealed record RecuperarSnapshotAposReinicioR24(ContextoSnapshotR24 Contexto);
public sealed record ResolverCommitIndeterminadoR24(ContextoSnapshotR24 Contexto,Guid AttemptId,string ExpectedSnapshotFingerprint);
public sealed record RecuperacaoSnapshotR24(SnapshotConsolidadoR24? Ativo,ImmutableArray<Guid> AbandonedAttempts,int RestartPageIndex)
{
    public bool RealProcessDurability => false;
}
public interface ISnapshotMasterdataStoreR24
{
    Task<ResultadoSnapshotR24<TentativaSnapshotR24>> IniciarTentativaAsync(IniciarTentativaSnapshotR24 input,CancellationToken ct);
    Task<ResultadoSnapshotR24<TentativaSnapshotR24>> RegistrarLoteAsync(RegistrarLoteSnapshotR24 input,CancellationToken ct);
    Task<ResultadoSnapshotR24<TentativaSnapshotR24>> ValidarCompletudeAsync(ValidarCompletudeSnapshotR24 input,CancellationToken ct);
    Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> PromoverSnapshotAsync(PromoverSnapshotR24 input,CancellationToken ct);
    Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> CarregarSnapshotAtivoAsync(CarregarSnapshotAtivoR24 input,CancellationToken ct);
    Task<ResultadoSnapshotR24<TentativaSnapshotR24>> AbandonarTentativaAsync(AbandonarTentativaSnapshotR24 input,CancellationToken ct);
    Task<ResultadoSnapshotR24<RecuperacaoSnapshotR24>> RecuperarAposReinicioAsync(RecuperarSnapshotAposReinicioR24 input,CancellationToken ct);
    Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> ResolverCommitIndeterminadoAsync(ResolverCommitIndeterminadoR24 input,CancellationToken ct);
}
