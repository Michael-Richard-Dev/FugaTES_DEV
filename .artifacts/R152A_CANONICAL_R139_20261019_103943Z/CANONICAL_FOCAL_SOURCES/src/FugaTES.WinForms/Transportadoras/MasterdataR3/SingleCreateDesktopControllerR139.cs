using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;

namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

public interface ISingleCreateDesktopSessionR139 : IAsyncDisposable
{
    ProntidaoSingleCreateR122 Readiness(string bp);
    Task<SingleCreateResultR31> ExecuteAsync(Guid attemptId,CancellationToken ct);
}

// No timer/worker. Both preflight and execution use the application's existing shared lock.
public sealed class SingleCreateDesktopControllerR139(OrquestradorCompartilhadoMasterdataR3 shared,
    Func<bool> unitaryMode,Func<string,ProntidaoSingleCreateR122> prerequisites,
    Func<string,CancellationToken,Task<ISingleCreateDesktopSessionR139>> prepare) : IAsyncDisposable
{
    ISingleCreateDesktopSessionR139? session;
    bool disposed;
    public bool UnitaryOnly { get {try{return unitaryMode();}catch{return true;}} }
    public bool Ready=>!disposed&&Readiness("20000014").Pronto;
    public bool CanValidate(string bp)
    {
        try{return !disposed&&UnitaryOnly&&prerequisites(bp).Pronto;}
        catch{return false;}
    }
    public ProntidaoSingleCreateR122 Readiness(string bp)
    {
        try
        {
            if(disposed)return new(false,["APP_CLOSED_OR_CANCELLED"]);
            if(!UnitaryOnly||!prerequisites(bp).Pronto)return new(false,["SINGLE_CREATE_APPROVAL_REVOKED_OR_INVALID"]);
            return session?.Readiness(bp)??new(false,["SINGLE_CREATE_EXPLICIT_PREFLIGHT_REQUIRED"]);
        }
        catch{return new(false,["SINGLE_CREATE_READINESS_FAILED"]);}
    }
    public async Task<string> ValidateAsync(string bp,CancellationToken ct=default)
    {
        if(!CanValidate(bp))return "SINGLE_CREATE_PREREQUISITES_BLOCKED";
        try
        {
            var result=await shared.ExecutarConsultaProtegidaAsync(async token=>
            {
                if(session is not null){await session.DisposeAsync();session=null;}
                session=await prepare(bp,token).ConfigureAwait(false);
                return session.Readiness(bp).Resumo;
            },ct).ConfigureAwait(false);
            return result.Resultado??"SINGLE_CREATE_PREFLIGHT_BUSY_OR_CANCELLED";
        }
        catch{return "SINGLE_CREATE_PREFLIGHT_FAILED";}
    }
    public Task<SingleCreateResultR31> ExecuteAsync(string bp,CancellationToken ct=default)
    {
        var id=Guid.NewGuid();
        return ExecuteProtectedAsync();
        async Task<SingleCreateResultR31> ExecuteProtectedAsync()
        {
            try{return await shared.ExecutarCriacaoUnitariaAsync(id,async token=>
            {
                if(!Readiness(bp).Pronto||session is null)return new(id,SingleCreateStateR31.BLOCKED,"SINGLE_CREATE_PREFLIGHT_REQUIRED");
                return await session.ExecuteAsync(id,token).ConfigureAwait(false);
            },ct).ConfigureAwait(false);}
            catch{return new(id,SingleCreateStateR31.INDETERMINATE,"SINGLE_CREATE_EXECUTION_UNKNOWN_NO_RETRY");}
        }
    }
    public async ValueTask DisposeAsync()
    {
        disposed=true;
        await shared.AguardarOciosidadeAsync().ConfigureAwait(false);
        if(session is not null){await session.DisposeAsync();session=null;}
    }
}

// Keeps the last unitary outcome visible across generic UI refreshes; identifiers/codes only.
public sealed class SingleCreateUiStatusR139
{
    public string Text {get;private set;}="";
    public void Preflight(string code)
    {
        try{FugaTES.Aplicacao.Transportadoras.CarrierPersistencePolicy.ValidateCode(code);}
        catch{code="SINGLE_CREATE_PREFLIGHT_BLOCKED";}
        Text="PREFLIGHT CREATE: "+code;
    }
    public void Record(SingleCreateResultR31 result)
    {
        var code=result.Code;
        try{FugaTES.Aplicacao.Transportadoras.CarrierPersistencePolicy.ValidateCode(code);}
        catch{code="SINGLE_CREATE_OUTCOME_REQUIRES_REVIEW";}
        Text=$"CREATE UNITARIO: {result.State} | {code} | AttemptId={result.AttemptId}";
        if(result.State==SingleCreateStateR31.INDETERMINATE)Text+=" | RECONCILIACAO OBRIGATORIA; SEM RETRY AUTOMATICO";
    }
    public string Display(string genericStatus)=>string.IsNullOrEmpty(Text)?genericStatus:Text;
}
