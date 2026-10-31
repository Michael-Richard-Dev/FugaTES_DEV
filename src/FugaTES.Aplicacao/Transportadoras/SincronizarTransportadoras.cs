using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace FugaTES.Aplicacao.Transportadoras;
public sealed class SincronizarTransportadoras(ISapCarrierDiscovery sap, IGuardianCarrierGateway guardian, ICarrierSyncStore store, CarrierSyncPolicy policy, TimeProvider time, Func<string,CancellationToken,Task<CarrierPayload>> individual)
{
    public static string Hash(CarrierPayload payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
    public async Task<CarrierStoredCycle?> ExecutarCicloAsync(CancellationToken ct)
    {
        await using var lease=await store.TentarAdquirirCicloAsync(ct);
        if(lease is null)return null;
        using var loss=CancellationTokenSource.CreateLinkedTokenSource(ct,lease.PerdaToken);
        var token=loss.Token; var id=Guid.NewGuid(); var start=time.GetUtcNow();
        await lease.IniciarAsync(id,start,token);
        await lease.HeartbeatAsync(start,start.AddSeconds(30),token);
        using var heartbeatStop=CancellationTokenSource.CreateLinkedTokenSource(token);
        var heartbeat=MaintainHeartbeatAsync(lease,start,heartbeatStop.Token,loss);
        try
        {
        var results=new List<CarrierStoredBp>();
        var request=await lease.TomarPedidoAsync(token);
        if(request is not null)
        {
            if(policy.PodeSolicitar(request.Bp,request.AuthenticatedSid))
                results.Add(await ConsultarIndividualAsync(request.Bp,id,lease,token));
            await lease.ConcluirPedidoAsync(request.Id,results.Count==0?"CARRIER_NOT_AUTHORIZED":results[0].ResultCode,token);
        }
        else if(policy.IncrementalReleased)
        {
            var checkpoint=await store.CarregarCheckpointAsync(token);
            var cursor=new CarrierCursor(checkpoint.Watermark,checkpoint.UpperBound,checkpoint.PageCursor) {VisitedPages=checkpoint.VisitedPages};
            var page=await sap.LerPaginaAsync(cursor,token);
            if(cursor.UpperBound is null)
            {
                var frozen=page.End?page.NextCursor.Watermark:page.NextCursor.UpperBound;
                if(frozen is null)throw new CarrierGatewayException("SAP_DELTA_BOUND_MISSING");
                await lease.ConfirmarPaginaAsync(new CarrierStoredCheckpoint(cursor.Watermark,frozen,cursor.PageToken){VisitedPages=cursor.VisitedPages},token);
            }
            foreach(var payload in page.Carriers) results.Add(await ProcessarAsync(payload,id,lease,token));
            if(results.All(r=>r.Status=="SYNCED"))
                await lease.ConfirmarPaginaAsync(new CarrierStoredCheckpoint(page.NextCursor.Watermark,page.NextCursor.UpperBound,page.NextCursor.PageToken) {VisitedPages=page.NextCursor.VisitedPages},token);
        }
        else
            foreach(var bp in policy.ControlledBps.Distinct(StringComparer.Ordinal))
                results.Add(await ConsultarIndividualAsync(bp,id,lease,token));
        var cycle=new CarrierStoredCycle(id,start,time.GetUtcNow(),results.Count,
            results.Count(r=>r.Status=="SYNCED"&&r.Action=="CREATE"),results.Count(r=>r.Status=="SYNCED"&&r.Action=="UPDATE"),
            results.Count(r=>r.Status=="SYNCED"&&r.Action=="NO_ACTION"),results.Count(r=>r.Status=="INDETERMINATE"),
            results.Count(r=>r.Status!="SYNCED"&&r.Status!="INDETERMINATE"),results.All(r=>r.Status=="SYNCED")?"SUCCESS":"PARTIAL");
        await lease.ConcluirAsync(cycle,token);
        await lease.HeartbeatAsync(time.GetUtcNow(),NextDue(start),token);
        return cycle;
        }
        finally {heartbeatStop.Cancel();await heartbeat;}
    }
    DateTimeOffset NextDue(DateTimeOffset start) => start.AddTicks((Math.Max(0,(time.GetUtcNow()-start).Ticks)/TimeSpan.FromSeconds(30).Ticks+1)*TimeSpan.FromSeconds(30).Ticks);
    async Task MaintainHeartbeatAsync(ICarrierCycleLease lease,DateTimeOffset start,CancellationToken ct,CancellationTokenSource loss)
    {
        try {while(true) {await Task.Delay(TimeSpan.FromSeconds(30),time,ct);await lease.HeartbeatAsync(time.GetUtcNow(),NextDue(start),ct);}}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch {loss.Cancel();}
    }
    async Task<CarrierStoredBp> ConsultarIndividualAsync(string bp,Guid cycle,ICarrierCycleLease lease,CancellationToken ct)
    {
        CarrierPayload payload;
        try {payload=await individual(bp,ct);}
        catch(OperationCanceledException){throw;}
        catch
        {
            var prior=await store.CarregarEstadoAsync(bp,ct);
            var failed=new CarrierStoredBp(bp,prior?.Name??"",prior?.GuardianCode,prior?.ContentHash??new string('0',64),
                prior?.PendingAttemptId is null?"BLOCKED":"INDETERMINATE",prior?.Action??"NO_ACTION","SAP_QUERY_FAILED",time.GetUtcNow(),prior?.GuardianSyncedUtc,prior?.PendingAttemptId);
            await lease.RegistrarEtapaAsync(new(cycle,bp,"SAP_GET","SAP_QUERY_FAILED",time.GetUtcNow()),ct);
            await lease.ConcluirBpAsync(failed,ct);return failed;
        }
        if(payload.BusinessPartner!=bp)throw new CarrierGatewayException("SAP_IDENTITY_MISMATCH");
        return await ProcessarAsync(payload,cycle,lease,ct);
    }
    async Task<CarrierStoredBp> ProcessarAsync(CarrierPayload payload,Guid cycle,ICarrierCycleLease lease,CancellationToken ct)
    {
        var bp=payload.BusinessPartner; CarrierPersistencePolicy.ValidateBp(bp);
        var expected=Hash(payload); var previous=await store.CarregarEstadoAsync(bp,ct);
        Guid? pending=previous?.PendingAttemptId; string? code=previous?.GuardianCode; var action="NO_ACTION";
        async Task Log(string stage,string result)=>await lease.RegistrarEtapaAsync(new(cycle,bp,stage,result,time.GetUtcNow()),ct);
        async Task<CarrierStoredBp> Finish(string status,string result,bool confirmed=false)
        {
            var row=new CarrierStoredBp(bp,payload.Name,code,pending is not null?previous?.ContentHash??expected:expected,status,action,result,time.GetUtcNow(),confirmed?time.GetUtcNow():previous?.GuardianSyncedUtc,pending);
            await lease.ConcluirBpAsync(row,ct); await Log("RESULTADO_FINAL",result); return row;
        }
        await Log("SAP_GET","SUCCESS");
        if(string.IsNullOrWhiteSpace(payload.Name)||string.IsNullOrWhiteSpace(payload.LegalName)||payload.Name.Length>50||payload.LegalName.Length>50)
            return await Finish(pending is null?"BLOCKED":"INDETERMINATE","CARRIER_PAYLOAD_INVALID");
        CarrierLookup lookup;
        try {lookup=await guardian.ConsultarAsync(bp,ct);}
        catch(OperationCanceledException){throw;}
        catch {return await Finish(pending is null?"BLOCKED":"INDETERMINATE","GUARDIAN_GET_FAILED");}
        await Log("GUARDIAN_GET",lookup.State==CarrierLookupState.Unique||lookup.State==CarrierLookupState.Absent?"SUCCESS":"LOOKUP_BLOCKED");
        if(lookup.State==CarrierLookupState.Unique)
        {
            if(lookup.Payload?.BusinessPartner!=bp||string.IsNullOrWhiteSpace(lookup.GuardianCode))return await Finish(pending is null?"BLOCKED":"INDETERMINATE","GUARDIAN_IDENTITY_MISMATCH");
            if(pending is not null && previous!.GuardianCode is not null && previous.GuardianCode!=lookup.GuardianCode) return await Finish("INDETERMINATE","GUARDIAN_PENDING_IDENTITY_MISMATCH");
            code=lookup.GuardianCode;
            if(Hash(lookup.Payload)==expected&&(pending is null||previous!.ContentHash==expected))
            {
                await Log("GUARDIAN_POST","NOT_EXECUTED");
                // This GET is also the reconciliation readback after a prior uncertain write.
                await Log("GUARDIAN_READBACK","CONFIRMED");pending=null;
                return await Finish("SYNCED","NO_ACTION",true);
            }
        }
        if(pending is not null)return await Finish("INDETERMINATE","CARRIER_PENDING_RECONCILIATION");
        if(lookup.State is CarrierLookupState.Ambiguous or CarrierLookupState.Failed)return await Finish("BLOCKED","GUARDIAN_LOOKUP_BLOCKED");
        if(!policy.PodeEscrever(bp))return await Finish("BLOCKED","CARRIER_NOT_AUTHORIZED");
        action=lookup.State==CarrierLookupState.Absent?"CREATE":"UPDATE";
        await lease.VerificarAtivaAsync(ct);
        if(!policy.PodeEscrever(bp))return await Finish("BLOCKED","CARRIER_NOT_AUTHORIZED");
        var attempt=new CarrierWriteAttempt(Guid.NewGuid(),bp,code,action,payload);
        if(!await lease.RegistrarAntesDoWriteAsync(new(attempt.Id,cycle,bp,action,expected,code,time.GetUtcNow()),ct))
            throw new CarrierGatewayException("CARRIER_PENDING_RECONCILIATION");
        pending=attempt.Id; previous=new(bp,payload.Name,code,expected,"WRITE_IN_FLIGHT",action,"WRITE_IN_FLIGHT",time.GetUtcNow(),previous?.GuardianSyncedUtc,pending);
        CarrierWriteResult write;
        try
        {
            await lease.VerificarAtivaAsync(ct);
            if(!policy.PodeEscrever(bp))return await Finish("INDETERMINATE","CARRIER_AUTH_EXPIRED_BEFORE_SEND");
            write=await guardian.GravarAsync(attempt,ct);
        }
        catch(OperationCanceledException){throw;}
        catch {return await Finish("INDETERMINATE","GUARDIAN_WRITE_UNCERTAIN");}
        await Log(action=="CREATE"?"GUARDIAN_POST":"GUARDIAN_PUT",write.Indeterminate?"INDETERMINATE":write.Accepted?"ACCEPTED":"WRITE_REJECTED");
        if(!write.Accepted||write.Indeterminate)return await Finish("INDETERMINATE","GUARDIAN_WRITE_UNCERTAIN");
        code=write.GuardianCode??code;
        if(string.IsNullOrWhiteSpace(code))return await Finish("INDETERMINATE","GUARDIAN_READBACK_CODE_MISSING");
        CarrierLookup readback;
        try {readback=await guardian.LerConfirmacaoAsync(code,ct);}
        catch(OperationCanceledException){throw;}
        catch {return await Finish("INDETERMINATE","GUARDIAN_READBACK_FAILED");}
        if(readback.State!=CarrierLookupState.Unique||readback.GuardianCode!=code||readback.Payload?.BusinessPartner!=bp||Hash(readback.Payload)!=expected)
        {await Log("GUARDIAN_READBACK","MISMATCH");return await Finish("INDETERMINATE","GUARDIAN_READBACK_MISMATCH");}
        await Log("GUARDIAN_READBACK","CONFIRMED");pending=null;return await Finish("SYNCED","CONFIRMED",true);
    }
}