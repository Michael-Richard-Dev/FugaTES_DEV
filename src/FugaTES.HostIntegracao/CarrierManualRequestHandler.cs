using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.HostIntegracao;
public sealed record CarrierManualResponse(bool Accepted,string Code);
public sealed class CarrierManualRequestHandler(CarrierSyncPolicy policy,Func<CarrierManualRequest,CancellationToken,Task<bool>> enqueue,TimeProvider clock)
{
    public async Task<CarrierManualResponse> HandleAsync(Guid id,string bp,string authenticatedSid,CancellationToken ct)
    {
        if(id==Guid.Empty)return new(false,"CARRIER_REQUEST_NOT_READY");
        try {CarrierPersistencePolicy.ValidateBp(bp);}catch{return new(false,"CARRIER_REQUEST_NOT_READY");}
        if(!policy.PodeSolicitar(bp,authenticatedSid))return new(false,"CARRIER_REQUEST_NOT_AUTHORIZED");
        try {return await enqueue(new(id,bp,authenticatedSid,clock.GetUtcNow()),ct)?new(true,"CARRIER_REQUEST_ACCEPTED"):new(false,"CARRIER_REQUEST_STATUS_UNKNOWN");}
        catch(OperationCanceledException){throw;}
        catch{return new(false,"CARRIER_REQUEST_STATUS_UNKNOWN");}
    }
}