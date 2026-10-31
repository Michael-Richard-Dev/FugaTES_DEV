using System.Collections.Concurrent;
using System.Collections.Immutable;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
namespace FugaTES.Persistencia.Transportadoras.Fakes;

public enum FalhaPromocaoFakeR24 { None, BeforeCommit, DuringCopy, AfterCommitUnknown }
public enum CorrupcaoSnapshotFakeR24 { Fingerprint, Version, Row }

// Test-only simulated storage lifetime. No files, SQL, Npgsql or external connectors.
public sealed class SnapshotMemoryStorageR24
{
    readonly object catalog=new();
    readonly Dictionary<Guid,ScopeState> scopes=[];
    readonly Dictionary<string,Guid> fingerprints=new(StringComparer.Ordinal);
    internal readonly ConcurrentDictionary<Guid,Guid> AttemptOwners=new();
    internal ScopeState? Find(EscopoSnapshotR24 scope,bool create)
    {
        lock(catalog)
        {
            if(scopes.TryGetValue(scope.ScopeId,out var existing))return existing.Scope==scope?existing:null;
            if(!create)return null;
            if(fingerprints.TryGetValue(scope.ScopeFingerprint,out var owner)&&owner!=scope.ScopeId)return null;
            var state=new ScopeState(scope);scopes.Add(scope.ScopeId,state);fingerprints[scope.ScopeFingerprint]=scope.ScopeId;return state;
        }
    }
    public void FalharProximaPromocao(Guid scope,FalhaPromocaoFakeR24 failure)
    {ScopeState s;lock(catalog)s=scopes[scope];lock(s.Gate)s.Fault=failure;}
    public void AntesDoProximoCommitDePromocao(Guid scope,Action probe)
    {ScopeState s;lock(catalog)s=scopes[scope];lock(s.Gate)s.Probe=probe;}
    // Deliberate corruption injection is outside the repository interface, for behavioral QA only.
    public void CorromperAtivo(Guid scope,CorrupcaoSnapshotFakeR24 corruption)
    {
        ScopeState s;lock(catalog)s=scopes[scope];lock(s.Gate)
        {
            var id=s.Data.Pointer.SnapshotId!.Value;var old=s.Data.Snapshots[id];
            var bad=corruption switch
            {
                CorrupcaoSnapshotFakeR24.Version=>old with{Versoes=old.Versoes with{Contrato=999}},
                CorrupcaoSnapshotFakeR24.Row=>old with{Rows=old.Rows.SetItem(0,old.Rows[0] with{RowFingerprint=new string('0',64)})},
                _=>old with{SnapshotFingerprint=new string('0',64)}
            };
            s.Data=s.Data with{Snapshots=s.Data.Snapshots.SetItem(id,bad)};
        }
    }
    internal sealed class ScopeState(EscopoSnapshotR24 scope)
    {
        public readonly object Gate=new();public readonly EscopoSnapshotR24 Scope=scope;
        public DataRoot Data=DataRoot.Empty;public FalhaPromocaoFakeR24 Fault;public Action? Probe;
    }
    internal sealed record Attempt(Guid Id,ContextoSnapshotR24 Context,DateTimeOffset Reference,
        EstadoTentativaSnapshotR24 Status,PonteiroSnapshotR24 Base,
        ImmutableSortedDictionary<int,PaginaSnapshotR24> Pages,
        ImmutableSortedDictionary<string,RegistroBpSnapshotR24> Rows,int? ExpectedCount=null,string? Fingerprint=null,string? Failure=null,DateTimeOffset? ValidatedAt=null);
    internal sealed record Audit(Guid AttemptId,string EventCode,string? FailureCode=null);
    internal sealed record DataRoot(ImmutableDictionary<Guid,Attempt> Attempts,
        ImmutableDictionary<Guid,SnapshotConsolidadoR24> Snapshots,PonteiroSnapshotR24 Pointer,ImmutableArray<Audit> Events)
    {
        public static DataRoot Empty {get;}=new(ImmutableDictionary<Guid,Attempt>.Empty,
            ImmutableDictionary<Guid,SnapshotConsolidadoR24>.Empty,PonteiroSnapshotR24.Vazio,[]);
    }
}

public sealed class SnapshotMasterdataMemoryFakeR24(SnapshotMemoryStorageR24 storage,TimeProvider? clock=null,
    ContextoTemporalSinteticoSnapshotR25D? syntheticContext=null):ISnapshotMasterdataStoreR24
{
    static bool Digest(string? s)=>s is {Length:64}&&s.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool Identifier(string s)=>!string.IsNullOrWhiteSpace(s)&&s.Length<=64&&s.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-');
    static string? ContextProblem(ContextoSnapshotR24 c)
    {
        if(c.Versoes.Normalizacao!=VersoesSnapshotR24.PropostaAtual.Normalizacao)return "CANONICALIZATION_VERSION_MISMATCH";
        if(!SnapshotFingerprintR24.Supported(c.Versoes))return "VERSION_INCOMPATIBLE";
        var s=c.Escopo;
        if(s.ScopeId==Guid.Empty||!Identifier(s.SapSourceCode)||!Identifier(s.SapClientCode)||!Identifier(s.QueryContractVersion)||
            !Digest(s.ScopeFingerprint)||SnapshotFingerprintR24.Scope(s)!=s.ScopeFingerprint)return "SCOPE_MISMATCH";
        return null;
    }
    static TentativaSnapshotR24 View(SnapshotMemoryStorageR24.Attempt a)=>new(a.Id,a.Context.Escopo.ScopeId,a.Status,a.Pages.Count,a.Rows.Count,a.Reference,a.Fingerprint,a.Base);
    static ResultadoSnapshotR24<T> Result<T>(ContextoSnapshotR24 c,Guid? id,SituacaoSnapshotR24 state,string code,T? value=default)
        =>new(state,code,c.Escopo.ScopeId,id,value);
    Task<ResultadoSnapshotR24<T>> Run<T>(ContextoSnapshotR24 c,Guid? id,CancellationToken ct,bool create,bool mutate,
        Func<SnapshotMemoryStorageR24.ScopeState,ResultadoSnapshotR24<T>> action)
    {
        try
        {
            if(ContextProblem(c) is {} problem)return Task.FromResult(Result<T>(c,id,SituacaoSnapshotR24.BLOCKED,problem));
            var scope=storage.Find(c.Escopo,create&&!ct.IsCancellationRequested);
            if(scope is null)return Task.FromResult(Result<T>(c,id,ct.IsCancellationRequested?SituacaoSnapshotR24.CANCELED:SituacaoSnapshotR24.NOT_FOUND,
                ct.IsCancellationRequested?"CANCELED":"SEM_SNAPSHOT_COMPATIVEL"));
            lock(scope.Gate)
            {
                void CancelAttempt()
                {
                    if(mutate&&id is {} key&&scope.Data.Attempts.TryGetValue(key,out var a)&&
                        a.Status is EstadoTentativaSnapshotR24.SCANNING or EstadoTentativaSnapshotR24.ENRICHING or EstadoTentativaSnapshotR24.READY)
                        SaveAttempt(scope,a with{Status=EstadoTentativaSnapshotR24.ABANDONED,Failure="CANCELED"},"ABANDONED");
                }
                void UndoUncommittedOwner()
                {
                    if(create&&id is {} key&&!scope.Data.Attempts.ContainsKey(key))
                        storage.AttemptOwners.TryRemove(new KeyValuePair<Guid,Guid>(key,c.Escopo.ScopeId));
                }
                if(ct.IsCancellationRequested)
                {CancelAttempt();return Task.FromResult(Result<T>(c,id,SituacaoSnapshotR24.CANCELED,"CANCELED"));}
                // All writes are staged privately, including pages, events and promotion.
                // Only one root-reference swap publishes a committed unit to other instances.
                var staged=new SnapshotMemoryStorageR24.ScopeState(scope.Scope){Data=scope.Data,Fault=scope.Fault,Probe=scope.Probe};
                try
                {
                    var result=action(staged);
                    ct.ThrowIfCancellationRequested();
                    scope.Data=staged.Data;
                    return Task.FromResult(result);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested)
                {UndoUncommittedOwner();CancelAttempt();return Task.FromResult(Result<T>(c,id,SituacaoSnapshotR24.CANCELED,"CANCELED"));}
                catch
                {UndoUncommittedOwner();return Task.FromResult(Result<T>(c,id,SituacaoSnapshotR24.BLOCKED,"INTERNAL_OFFLINE_FAILURE"));}
                finally {scope.Fault=staged.Fault;scope.Probe=staged.Probe;}
            }
        }
        catch{return Task.FromResult(Result<T>(c,id,SituacaoSnapshotR24.BLOCKED,"INTERNAL_OFFLINE_FAILURE"));}
    }
    static void SaveAttempt(SnapshotMemoryStorageR24.ScopeState s,SnapshotMemoryStorageR24.Attempt a,string code)
        =>s.Data=s.Data with{Attempts=s.Data.Attempts.SetItem(a.Id,a),Events=s.Data.Events.Add(new(a.Id,code,a.Failure))};
    string? FindAttempt(SnapshotMemoryStorageR24.ScopeState s,ContextoSnapshotR24 c,Guid id,out SnapshotMemoryStorageR24.Attempt? a)
    {
        a=null;
        if(storage.AttemptOwners.TryGetValue(id,out var scope)&&scope!=c.Escopo.ScopeId)return "SCOPE_MISMATCH";
        if(!s.Data.Attempts.TryGetValue(id,out a))return "ATTEMPT_NOT_FOUND";
        return a.Context==c?null:"SCOPE_OR_VERSION_MISMATCH";
    }
    public Task<ResultadoSnapshotR24<TentativaSnapshotR24>> IniciarTentativaAsync(IniciarTentativaSnapshotR24 input,CancellationToken ct)
        =>Run<TentativaSnapshotR24>(input.Contexto,input.AttemptId,ct,true,true,s=>
        {
            var c=input.Contexto;
            if(input.AttemptId==Guid.Empty)return Result<TentativaSnapshotR24>(c,input.AttemptId,SituacaoSnapshotR24.BLOCKED,"ATTEMPT_ID_INVALID");
            if(s.Data.Attempts.TryGetValue(input.AttemptId,out var old))
                return old.Context==c&&old.Reference==input.ReferenceAt?Result(c,input.AttemptId,SituacaoSnapshotR24.REPLAY,"ATTEMPT_EXISTS",View(old)):
                    Result<TentativaSnapshotR24>(c,input.AttemptId,SituacaoSnapshotR24.BLOCKED,"ATTEMPT_IDENTITY_MISMATCH");
            if(!storage.AttemptOwners.TryAdd(input.AttemptId,c.Escopo.ScopeId))return Result<TentativaSnapshotR24>(c,input.AttemptId,SituacaoSnapshotR24.BLOCKED,"ATTEMPT_ID_ALREADY_USED");
            var a=new SnapshotMemoryStorageR24.Attempt(input.AttemptId,c,input.ReferenceAt,EstadoTentativaSnapshotR24.SCANNING,s.Data.Pointer,
                ImmutableSortedDictionary<int,PaginaSnapshotR24>.Empty,
                ImmutableSortedDictionary.Create<string,RegistroBpSnapshotR24>(StringComparer.Ordinal));
            SaveAttempt(s,a,"STARTED");return Result(c,input.AttemptId,SituacaoSnapshotR24.SUCCESS,"STARTED",View(a));
        });
    bool RowValid(ContextoSnapshotR24 c,RegistroBpSnapshotR24 row)
    {
        try{CarrierPersistencePolicy.ValidateBp(row.BusinessPartner);}
        catch{return false;}
        if(row.ApprovedFullNameNormalized is not null)return false;
        return Enum.IsDefined(row.Crm010TemporalEvaluation)&&Digest(row.RowFingerprint)&&row.RowFingerprint==SnapshotFingerprintR24.Row(c.Versoes,row);
    }
    public Task<ResultadoSnapshotR24<TentativaSnapshotR24>> RegistrarLoteAsync(RegistrarLoteSnapshotR24 input,CancellationToken ct)
        =>Run<TentativaSnapshotR24>(input.Contexto,input.AttemptId,ct,false,true,s=>
        {
            var c=input.Contexto;
            if(FindAttempt(s,c,input.AttemptId,out var found) is {} error)return Result<TentativaSnapshotR24>(c,input.AttemptId,SituacaoSnapshotR24.BLOCKED,error);
            var a=found!;
            if(a.Status is not (EstadoTentativaSnapshotR24.SCANNING or EstadoTentativaSnapshotR24.ENRICHING))
                return Result<TentativaSnapshotR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"ATTEMPT_IMMUTABLE");
            ResultadoSnapshotR24<TentativaSnapshotR24> Reject(string code)
            {var rejected=a with{Status=EstadoTentativaSnapshotR24.REJECTED,Failure=code};SaveAttempt(s,rejected,"REJECTED");return Result(c,a.Id,SituacaoSnapshotR24.BLOCKED,code,View(rejected));}
            if(input.Modo==ModoLoteSnapshotR24.APPEND_PAGE)
            {
                var p=input.Pagina;
                if(p is null||!input.EnrichmentRows.IsEmpty||p.Rows.IsDefault||p.PageIndex<0||p.ObservedOffset<0||p.RequestedTop<=0||p.DeclaredRowCount<0||
                    p.DeclaredRowCount!=p.Rows.Length||p.Rows.Length>p.RequestedTop)return Reject("PAGE_SHAPE_INVALID");
                // Detach even arrays wrapped by a caller as ImmutableArray: retained pages own their bytes/records.
                p=p with{Rows=p.Rows.ToArray().ToImmutableArray()};
                if(a.Pages.ContainsKey(p.PageIndex)||a.Pages.Values.Any(v=>v.ObservedOffset==p.ObservedOffset))return Reject("DUPLICATE_PAGE");
                if(a.Pages.Values.Any(v=>v.TerminalPage))return Reject("PAGE_AFTER_TERMINAL");
                if(!Digest(p.PageFingerprint)||p.PageFingerprint!=SnapshotFingerprintR24.Page(p))return Reject("PAGE_FINGERPRINT_INVALID");
                var rows=a.Rows;
                foreach(var row in p.Rows)
                {
                    if(row.ApprovedFullNameNormalized is not null)return Reject("FULL_NAME_RETENTION_NOT_APPROVED");
                    if(!TemporalPolicyAvailable(c)&&row.Crm010TemporalEvaluation!=AvaliacaoCrm010R24.INDETERMINATE)return Reject("TEMPORAL_POLICY_NOT_APPROVED");
                    if(!RowValid(c,row))return Reject("ROW_FINGERPRINT_OR_ID_INVALID");
                    if(rows.ContainsKey(row.BusinessPartner))return Reject("DUPLICATE_BP");
                    rows=rows.Add(row.BusinessPartner,row);
                }
                var next=a with{Rows=rows,Pages=a.Pages.Add(p.PageIndex,p),Status=p.TerminalPage?EstadoTentativaSnapshotR24.ENRICHING:a.Status};
                SaveAttempt(s,next,"PAGE_OBSERVED");return Result(c,a.Id,SituacaoSnapshotR24.SUCCESS,"PAGE_OBSERVED",View(next));
            }
            if(input.Modo!=ModoLoteSnapshotR24.VALIDATE_ENRICHMENT||input.Pagina is not null||input.EnrichmentRows.IsDefaultOrEmpty)
                return Reject("ENRICHMENT_BATCH_INVALID");
            var updated=a.Rows;var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var row in input.EnrichmentRows.ToArray())
            {
                if(row.ApprovedFullNameNormalized is not null)return Reject("FULL_NAME_RETENTION_NOT_APPROVED");
                if(!TemporalPolicyAvailable(c)&&row.Crm010TemporalEvaluation!=AvaliacaoCrm010R24.INDETERMINATE)return Reject("TEMPORAL_POLICY_NOT_APPROVED");
                if(!seen.Add(row.BusinessPartner))return Reject("DUPLICATE_BP");
                if(!updated.TryGetValue(row.BusinessPartner,out var old)||old.EnrichmentValidated||!row.EnrichmentValidated||!RowValid(c,row))return Reject("ENRICHMENT_INVALID");
                if(old.RoleValidFromObserved!=row.RoleValidFromObserved||old.RoleValidToObserved!=row.RoleValidToObserved)return Reject("ROLE_OBSERVATION_CHANGED");
                updated=updated.SetItem(row.BusinessPartner,row);
            }
            var enriched=a with{Rows=updated,Status=EstadoTentativaSnapshotR24.ENRICHING};SaveAttempt(s,enriched,"ENRICHMENT_VALIDATED");
            return Result(c,a.Id,SituacaoSnapshotR24.SUCCESS,"ENRICHMENT_VALIDATED",View(enriched));
        });
    bool TemporalPolicyAvailable(ContextoSnapshotR24 c)=>c.Versoes==VersoesSnapshotR24.AprovadaR25T||
        c.Versoes==VersoesSnapshotR24.PropostaAtual&&syntheticContext is not null&&syntheticContext.MatchesScope(c.Escopo.ScopeId);
    bool TemporalValidated(ContextoSnapshotR24 c,RegistroBpSnapshotR24 r,DateTimeOffset reference)
    {
        if(!r.EnrichmentValidated||!TemporalPolicyAvailable(c))return false;
        var expected=c.Versoes==VersoesSnapshotR24.AprovadaR25T
            ?new Crm010BusinessDatePolicyR25T().Evaluate(r.RoleValidFromObserved,r.RoleValidToObserved,reference).Status
            :syntheticContext!.Evaluate(r.BusinessPartner,r.RoleValidFromObserved,r.RoleValidToObserved,reference);
        return expected!=AvaliacaoCrm010R24.INDETERMINATE&&r.Crm010TemporalEvaluation==expected;
    }
    string? Complete(SnapshotMemoryStorageR24.Attempt a,int count,string fingerprint)
    {
        if(!TemporalPolicyAvailable(a.Context))return "TEMPORAL_POLICY_NOT_APPROVED";
        if(a.Failure is not null||count<0||a.Pages.Count==0||a.Pages.Values.Count(p=>p.TerminalPage)!=1||!a.Pages.Last().Value.TerminalPage)return "SCAN_NOT_COMPLETE";
        var top=a.Pages.First().Value.RequestedTop;var keys=new HashSet<string>(StringComparer.Ordinal);int index=0;long total=0;string? lastBp=null;
        foreach(var p in a.Pages.Values)
        {
            if(p.PageIndex!=index||p.ObservedOffset!=(long)index*top||p.RequestedTop!=top||p.DeclaredRowCount!=p.Rows.Length||
                !p.TerminalPage&&p.Rows.Length==0||p.PageFingerprint!=SnapshotFingerprintR24.Page(p))return "PAGE_CONTINUITY_OR_HASH_INVALID";
            foreach(var row in p.Rows)
            {
                if(lastBp is not null&&StringComparer.Ordinal.Compare(row.BusinessPartner,lastBp)<=0)return "PAGE_ORDERING_INVALID";
                lastBp=row.BusinessPartner;
                if(!keys.Add(row.BusinessPartner)||!a.Rows.TryGetValue(row.BusinessPartner,out var current)||
                    row.RoleValidFromObserved!=current.RoleValidFromObserved||row.RoleValidToObserved!=current.RoleValidToObserved)return "PAGE_BP_INTEGRITY_INVALID";
            }
            total+=p.DeclaredRowCount;index++;
        }
        if(total!=count||a.Rows.Count!=count||keys.Count!=count)return "BP_COUNT_MISMATCH";
        if(a.Rows.Values.Any(r=>!RowValid(a.Context,r)||!TemporalValidated(a.Context,r,a.Reference)))return "ENRICHMENT_OR_TEMPORAL_INVALID";
        if(!Digest(fingerprint)||fingerprint!=SnapshotFingerprintR24.Snapshot(a.Context,a.Reference,a.Pages.Count,a.Rows.Values))return "SNAPSHOT_FINGERPRINT_INVALID";
        return null;
    }
    public Task<ResultadoSnapshotR24<TentativaSnapshotR24>> ValidarCompletudeAsync(ValidarCompletudeSnapshotR24 input,CancellationToken ct)
        =>Run<TentativaSnapshotR24>(input.Contexto,input.AttemptId,ct,false,true,s=>
        {
            var c=input.Contexto;if(FindAttempt(s,c,input.AttemptId,out var found) is {} error)return Result<TentativaSnapshotR24>(c,input.AttemptId,SituacaoSnapshotR24.BLOCKED,error);
            var a=found!;
            if(a.Status==EstadoTentativaSnapshotR24.READY&&a.ExpectedCount==input.ExpectedBpCount&&a.Fingerprint==input.ExpectedSnapshotFingerprint)
                return Result(c,a.Id,SituacaoSnapshotR24.REPLAY,"READY_REPLAY",View(a));
            if(a.Status is not (EstadoTentativaSnapshotR24.SCANNING or EstadoTentativaSnapshotR24.ENRICHING))return Result<TentativaSnapshotR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"ATTEMPT_IMMUTABLE");
            if(Complete(a,input.ExpectedBpCount,input.ExpectedSnapshotFingerprint) is {} invalid)
            {a=a with{Status=EstadoTentativaSnapshotR24.REJECTED,Failure=invalid};SaveAttempt(s,a,"REJECTED");return Result(c,a.Id,SituacaoSnapshotR24.BLOCKED,invalid,View(a));}
            a=a with{Status=EstadoTentativaSnapshotR24.READY,ExpectedCount=input.ExpectedBpCount,Fingerprint=input.ExpectedSnapshotFingerprint,ValidatedAt=(clock??TimeProvider.System).GetUtcNow().ToUniversalTime()};SaveAttempt(s,a,"READY");
            return Result(c,a.Id,SituacaoSnapshotR24.SUCCESS,"READY",View(a));
        });
    bool SnapshotValid(ContextoSnapshotR24 c,SnapshotConsolidadoR24 snap)
        =>TemporalPolicyAvailable(c)&&
            snap.ValidatedAt.Offset==TimeSpan.Zero&&snap.ConsolidatedAt.Offset==TimeSpan.Zero&&snap.ValidatedAt<=snap.ConsolidatedAt&&
            snap.ScopeId==c.Escopo.ScopeId&&snap.ScopeFingerprint==c.Escopo.ScopeFingerprint&&snap.Versoes==c.Versoes&&snap.Version>0&&snap.PageCount>0&&
            !snap.Rows.IsDefault&&snap.BpCount==snap.Rows.Length&&snap.Rows.Select(r=>r.BusinessPartner).Distinct(StringComparer.Ordinal).Count()==snap.Rows.Length&&
            snap.Rows.All(r=>RowValid(c,r)&&TemporalValidated(c,r,snap.ReferenceAt))&&Digest(snap.SnapshotFingerprint)&&
            snap.SnapshotFingerprint==SnapshotFingerprintR24.Snapshot(c,snap.ReferenceAt,snap.PageCount,snap.Rows);
    SnapshotConsolidadoR24? Active(SnapshotMemoryStorageR24.ScopeState s,ContextoSnapshotR24 c)
    {
        var pointer=s.Data.Pointer;
        if(pointer.SnapshotId is not {} id||!s.Data.Snapshots.TryGetValue(id,out var snap)||snap.Version!=pointer.ActiveVersion||!SnapshotValid(c,snap))return null;
        return snap;
    }
    public Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> PromoverSnapshotAsync(PromoverSnapshotR24 input,CancellationToken ct)
        =>Run<SnapshotConsolidadoR24>(input.Contexto,input.AttemptId,ct,false,true,s=>
        {
            var c=input.Contexto;if(FindAttempt(s,c,input.AttemptId,out var found) is {} error)return Result<SnapshotConsolidadoR24>(c,input.AttemptId,SituacaoSnapshotR24.BLOCKED,error);
            var a=found!;
            if(a.Status==EstadoTentativaSnapshotR24.PROMOTED)
            {
                var prior=s.Data.Snapshots.Values.SingleOrDefault(x=>x.AttemptId==a.Id);
                return prior is not null&&prior.SnapshotFingerprint==input.ExpectedSnapshotFingerprint&&SnapshotValid(c,prior)?
                    Result(c,a.Id,SituacaoSnapshotR24.REPLAY,"PROMOTION_REPLAY",prior):Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"PROMOTED_IDENTITY_INVALID");
            }
            if(a.Status!=EstadoTentativaSnapshotR24.READY||a.ExpectedCount is null||a.Fingerprint!=input.ExpectedSnapshotFingerprint||
                Complete(a,a.ExpectedCount.Value,input.ExpectedSnapshotFingerprint) is not null)return Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"ATTEMPT_NOT_PROMOTABLE");
            if(input.ExpectedBase!=a.Base||input.ExpectedBase!=s.Data.Pointer)
            {SaveAttempt(s,a with{Status=EstadoTentativaSnapshotR24.REJECTED,Failure="CONFLICT_STALE_BASE"},"CONFLICT_REJECTED");return Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.CONFLICT,"CONFLICT_STALE_BASE");}
            if(s.Data.Pointer.SnapshotId is not null&&Active(s,c) is null)return Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"ACTIVE_BASE_CORRUPT");
            var fault=s.Fault;s.Fault=FalhaPromocaoFakeR24.None;
            if(fault==FalhaPromocaoFakeR24.BeforeCommit)return Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"SIMULATED_CRASH_BEFORE_COMMIT");
            if(fault==FalhaPromocaoFakeR24.DuringCopy)return Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"SIMULATED_PROMOTION_ROLLBACK");
            var probe=s.Probe;s.Probe=null;probe?.Invoke();
            ct.ThrowIfCancellationRequested();
            var rows=a.Rows.Values.ToImmutableArray();
            var snapshot=new SnapshotConsolidadoR24(Guid.NewGuid(),c.Escopo.ScopeId,a.Id,checked(s.Data.Pointer.ActiveVersion+1),c.Escopo.ScopeFingerprint,c.Versoes,
                a.Reference,a.Pages.Count,a.ExpectedCount.Value,rows,a.Fingerprint!,
                a.ValidatedAt??throw new InvalidOperationException("VALIDATED_AT_MISSING"),(clock??TimeProvider.System).GetUtcNow().ToUniversalTime());
            if(!SnapshotValid(c,snapshot)||fault==FalhaPromocaoFakeR24.DuringCopy)
                return Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"SIMULATED_PROMOTION_ROLLBACK");
            var staged=s.Data with{Snapshots=s.Data.Snapshots.Add(snapshot.SnapshotId,snapshot),
                Attempts=s.Data.Attempts.SetItem(a.Id,a with{Status=EstadoTentativaSnapshotR24.PROMOTED}),
                Pointer=new(snapshot.SnapshotId,snapshot.Version),Events=s.Data.Events.Add(new(a.Id,"PROMOTED"))};
            ct.ThrowIfCancellationRequested();
            s.Data=staged; // One atomic simulated commit: no staged contents visible before this swap.
            return fault==FalhaPromocaoFakeR24.AfterCommitUnknown?Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.INDETERMINATE,"COMMIT_OUTCOME_UNKNOWN"):
                Result(c,a.Id,SituacaoSnapshotR24.SUCCESS,"PROMOTED",snapshot);
        });
    public async Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> CarregarSnapshotAtivoAsync(CarregarSnapshotAtivoR24 input,CancellationToken ct)
    {
        var result=await Run<SnapshotConsolidadoR24>(input.Contexto,null,ct,false,false,s=>
        {var snap=Active(s,input.Contexto);return snap is null?Result<SnapshotConsolidadoR24>(input.Contexto,null,SituacaoSnapshotR24.BLOCKED,"SEM_SNAPSHOT_COMPATIVEL"):
            Result(input.Contexto,snap.AttemptId,SituacaoSnapshotR24.SUCCESS,"ACTIVE_SNAPSHOT",snap);}).ConfigureAwait(false);
        return result.Situacao is SituacaoSnapshotR24.BLOCKED or SituacaoSnapshotR24.NOT_FOUND
            ?result with{Codigo="SEM_SNAPSHOT_COMPATIVEL",Dados=null}:result;
    }
    public Task<ResultadoSnapshotR24<TentativaSnapshotR24>> AbandonarTentativaAsync(AbandonarTentativaSnapshotR24 input,CancellationToken ct)
        =>Run<TentativaSnapshotR24>(input.Contexto,input.AttemptId,ct,false,true,s=>
        {
            var c=input.Contexto;if(FindAttempt(s,c,input.AttemptId,out var found) is {} error)return Result<TentativaSnapshotR24>(c,input.AttemptId,SituacaoSnapshotR24.BLOCKED,error);
            var a=found!;if(a.Status==EstadoTentativaSnapshotR24.PROMOTED)return Result<TentativaSnapshotR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"PROMOTED_ATTEMPT_IMMUTABLE");
            if(a.Status==EstadoTentativaSnapshotR24.ABANDONED)return Result(c,a.Id,SituacaoSnapshotR24.REPLAY,"ABANDONED_REPLAY",View(a));
            a=a with{Status=EstadoTentativaSnapshotR24.ABANDONED,Failure="ABANDONED"};SaveAttempt(s,a,"ABANDONED");return Result(c,a.Id,SituacaoSnapshotR24.SUCCESS,"ABANDONED",View(a));
        });
    public Task<ResultadoSnapshotR24<RecuperacaoSnapshotR24>> RecuperarAposReinicioAsync(RecuperarSnapshotAposReinicioR24 input,CancellationToken ct)
        =>Run<RecuperacaoSnapshotR24>(input.Contexto,null,ct,false,true,s=>
        {
            if(s.Data.Pointer.SnapshotId is {} activeId&&s.Data.Snapshots.TryGetValue(activeId,out var existing)&&existing.Versoes!=input.Contexto.Versoes)
                return Result<RecuperacaoSnapshotR24>(input.Contexto,null,SituacaoSnapshotR24.BLOCKED,"SEM_SNAPSHOT_COMPATIVEL");
            var ids=ImmutableArray.CreateBuilder<Guid>();var staged=s.Data;
            foreach(var a in s.Data.Attempts.Values.Where(x=>x.Context==input.Contexto&&x.Status is (EstadoTentativaSnapshotR24.SCANNING or EstadoTentativaSnapshotR24.ENRICHING or EstadoTentativaSnapshotR24.READY)))
            {ids.Add(a.Id);staged=staged with{Attempts=staged.Attempts.SetItem(a.Id,a with{Status=EstadoTentativaSnapshotR24.ABANDONED,Failure="RECOVERY_RESTART"}),Events=staged.Events.Add(new(a.Id,"RECOVERY_RESTART"))};}
            s.Data=staged;var active=Active(s,input.Contexto);
            if(s.Data.Pointer.SnapshotId is not null&&active is null)return Result<RecuperacaoSnapshotR24>(input.Contexto,null,SituacaoSnapshotR24.BLOCKED,"SEM_SNAPSHOT_COMPATIVEL");
            return Result(input.Contexto,null,SituacaoSnapshotR24.SUCCESS,"RECOVERY_PAGE_ZERO",new RecuperacaoSnapshotR24(active,ids.ToImmutable(),0));
        });
    public Task<ResultadoSnapshotR24<SnapshotConsolidadoR24>> ResolverCommitIndeterminadoAsync(ResolverCommitIndeterminadoR24 input,CancellationToken ct)
        =>Run<SnapshotConsolidadoR24>(input.Contexto,input.AttemptId,ct,false,false,s=>
        {
            var c=input.Contexto;if(FindAttempt(s,c,input.AttemptId,out var found) is {} error)return Result<SnapshotConsolidadoR24>(c,input.AttemptId,SituacaoSnapshotR24.BLOCKED,error);
            var a=found!;if(a.Status!=EstadoTentativaSnapshotR24.PROMOTED)return Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.INDETERMINATE,"COMMIT_NOT_OBSERVED");
            var snap=s.Data.Snapshots.Values.SingleOrDefault(x=>x.AttemptId==a.Id);
            if(snap is null||snap.SnapshotFingerprint!=input.ExpectedSnapshotFingerprint||!SnapshotValid(c,snap)||Active(s,c) is null)
                return Result<SnapshotConsolidadoR24>(c,a.Id,SituacaoSnapshotR24.BLOCKED,"COMMIT_READBACK_INVALID");
            return Result(c,a.Id,SituacaoSnapshotR24.REPLAY,s.Data.Pointer.SnapshotId==snap.SnapshotId?"COMMIT_READBACK_CONFIRMED":"COMMIT_CONFIRMED_HISTORICAL",snap);
        });
}
