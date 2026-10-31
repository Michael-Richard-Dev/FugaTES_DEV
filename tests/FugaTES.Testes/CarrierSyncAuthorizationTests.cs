using FugaTES.Aplicacao.Transportadoras;
using FugaTES.HostIntegracao;
namespace FugaTES.Testes;
public class CarrierSyncAuthorizationTests
{
    static CarrierSyncPolicy Policy()=>new(new("HML",true,true,DateTimeOffset.UtcNow.AddMinutes(1),["000001"],["S-1-5-21-1"]),TimeProvider.System);
    [Fact] public async Task Authenticated_authorized_request_is_durably_queued()
    {CarrierManualRequest? saved=null;var handler=new CarrierManualRequestHandler(Policy(),(r,_)=>{saved=r;return Task.FromResult(true);},TimeProvider.System);var id=Guid.NewGuid();var response=await handler.HandleAsync(id,"000001","S-1-5-21-1",default);Assert.True(response.Accepted);Assert.Equal(id,saved!.Id);Assert.Equal("S-1-5-21-1",saved.AuthenticatedSid);}
    [Fact] public async Task Forged_identity_does_not_queue()
    {var calls=0;var handler=new CarrierManualRequestHandler(Policy(),(_,_)=>{calls++;return Task.FromResult(true);},TimeProvider.System);Assert.False((await handler.HandleAsync(Guid.NewGuid(),"000001","FORGED",default)).Accepted);Assert.Equal(0,calls);}
    [Fact] public async Task Empty_request_id_does_not_queue()
    {var calls=0;var handler=new CarrierManualRequestHandler(Policy(),(_,_)=>{calls++;return Task.FromResult(true);},TimeProvider.System);Assert.False((await handler.HandleAsync(Guid.Empty,"000001","S-1-5-21-1",default)).Accepted);Assert.Equal(0,calls);}
}