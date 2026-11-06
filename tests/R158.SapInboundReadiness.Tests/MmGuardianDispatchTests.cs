using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FugaTES.Aplicacao.Contratos.Mm;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.Integracao.Guardian;
namespace R158.SapInboundReadiness.Tests;
public class MmGuardianDispatchTests
{
    internal sealed class Registry:IRegistroProtocolosMm
    {
        public readonly ConcurrentDictionary<string,RegistroProtocoloMm> Rows=new();
        public bool FailReserve,FailClose;
        public Task<IReadOnlyList<RegistroProtocoloMm>> LerAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<RegistroProtocoloMm>>(Rows.Values.ToArray());
        public Task<bool> TentarReservarAsync(RegistroProtocoloMm row,CancellationToken ct)
        {if(FailReserve)throw new IOException();return Task.FromResult(Rows.TryAdd(row.Chave,row));}
        public Task FecharAsync(Guid id,string key,ResultadoProtocoloMm result,CancellationToken ct)
        {if(FailClose)throw new IOException();Rows[key]=Rows[key] with{Estado=result.Estado,Http=result.Http,ProtocoloId=result.ProtocoloId};return Task.CompletedTask;}
    }
    internal sealed class Gateway: IEnviarProtocoloMmGateway
    {
        public int Calls;public Func<byte[],Task<ResultadoProtocoloMm>>? Handle;
        public Task<ResultadoProtocoloMm> EnviarAsync(byte[] payload,CancellationToken ct)
        {Interlocked.Increment(ref Calls);return Handle?.Invoke(payload)??Task.FromResult(new ResultadoProtocoloMm(EstadoProtocoloMm.Criado,201,123,"ok"));}
    }
    [Fact] public async Task Reserve_precedes_post_exact_bytes_are_durable_and_repeated_cycles_do_not_send()
    {
        var db=new Registry();var api=new Gateway();api.Handle=bytes=>{var row=Assert.Single(db.Rows.Values);Assert.Equal(row.JsonEnviado,Encoding.UTF8.GetString(bytes));Assert.Equal(EstadoProtocoloMm.Indeterminado,row.Estado);return Task.FromResult(new ResultadoProtocoloMm(EstadoProtocoloMm.Criado,201,1,"ok"));};
        var service=new EnvioAutomaticoMm(db,api,"https://example.invalid/mm");
        var result=await service.ProcessarAsync([MmGuardianProtocolTests.Linha()],default);
        await service.ProcessarAsync([MmGuardianProtocolTests.Linha()],default);
        Assert.Equal(1,result.Criados);Assert.Equal(1,api.Calls);Assert.Equal(EstadoProtocoloMm.Criado,Assert.Single(db.Rows.Values).Estado);
    }
    [Fact] public async Task Storage_failure_prevents_post()
    {var db=new Registry{FailReserve=true};var api=new Gateway();await Assert.ThrowsAsync<IOException>(()=>new EnvioAutomaticoMm(db,api,"https://example.invalid/mm").ProcessarAsync([MmGuardianProtocolTests.Linha()],default));Assert.Equal(0,api.Calls);}
    [Fact] public async Task Failure_saving_external_success_keeps_reservation_and_blocks_second_post()
    {var db=new Registry{FailClose=true};var api=new Gateway();var service=new EnvioAutomaticoMm(db,api,"https://example.invalid/mm");await Assert.ThrowsAsync<IOException>(()=>service.ProcessarAsync([MmGuardianProtocolTests.Linha()],default));db.FailClose=false;await service.ProcessarAsync([MmGuardianProtocolTests.Linha()],default);Assert.Equal(1,api.Calls);Assert.Equal(EstadoProtocoloMm.Indeterminado,Assert.Single(db.Rows.Values).Estado);}
    [Fact] public async Task Timeout_never_retries_automatically()
    {var db=new Registry();var api=new Gateway{Handle=_=>throw new TaskCanceledException()};var service=new EnvioAutomaticoMm(db,api,"https://example.invalid/mm");await service.ProcessarAsync([MmGuardianProtocolTests.Linha()],default);await service.ProcessarAsync([MmGuardianProtocolTests.Linha()],default);Assert.Equal(1,api.Calls);Assert.Equal(EstadoProtocoloMm.Indeterminado,Assert.Single(db.Rows.Values).Estado);}
    [Fact] public async Task Concurrent_senders_share_atomic_database_reservation()
    {var db=new Registry();var api=new Gateway();await Task.WhenAll(Enumerable.Range(0,5).Select(_=>new EnvioAutomaticoMm(db,api,"https://example.invalid/mm").ProcessarAsync([MmGuardianProtocolTests.Linha()],default)));Assert.Equal(1,api.Calls);}
    [Fact] public async Task Changed_existing_item_does_not_create_second_protocol()
    {var db=new Registry();var api=new Gateway();var service=new EnvioAutomaticoMm(db,api,"https://example.invalid/mm");var row=MmGuardianProtocolTests.Linha();await service.ProcessarAsync([row],default);await service.ProcessarAsync([row with{Material="7000001"}],default);Assert.Equal(1,api.Calls);}
    [Fact] public async Task Initial_existing_items_are_sent_in_bounded_batches()
    {var db=new Registry();var api=new Gateway();var service=new EnvioAutomaticoMm(db,api,"https://example.invalid/mm");var rows=Enumerable.Range(1,12).Select(i=>MmGuardianProtocolTests.Linha(item:i.ToString())).ToArray();await service.ProcessarAsync(rows,default);Assert.Equal(10,api.Calls);await service.ProcessarAsync(rows,default);Assert.Equal(12,api.Calls);}
    [Fact] public async Task Revoked_admission_prevents_new_post()
    {var db=new Registry();var api=new Gateway();await new EnvioAutomaticoMm(db,api,"https://example.invalid/mm").ProcessarAsync([MmGuardianProtocolTests.Linha()],default,()=>false);Assert.Empty(db.Rows);Assert.Equal(0,api.Calls);}
    [Theory][InlineData(EstadoProtocoloMm.Recusado,400)][InlineData(EstadoProtocoloMm.Recusado,401)][InlineData(EstadoProtocoloMm.Indeterminado,504)]
    public async Task Terminal_or_uncertain_result_never_reenters_automatic_queue(string state,int http)
    {var db=new Registry();var api=new Gateway{Handle=_=>Task.FromResult(new ResultadoProtocoloMm(state,http,null,"safe"))};var service=new EnvioAutomaticoMm(db,api,"https://example.invalid/mm");await service.ProcessarAsync([MmGuardianProtocolTests.Linha()],default);await service.ProcessarAsync([MmGuardianProtocolTests.Linha()],default);Assert.Equal(1,api.Calls);Assert.Equal(state,Assert.Single(db.Rows.Values).Estado);}
    [Fact] public void Postgres_registry_is_not_present_yet()=>Assert.NotNull(typeof(FugaTES.Persistencia.Sd.RegistroProtocolosSdPostgreSql).Assembly.GetType("FugaTES.Persistencia.Mm.RegistroProtocolosMmPostgreSql"));
    internal sealed class Transport(HttpStatusCode status,string body):HttpMessageHandler
    {public int Calls;public byte[]? Sent;protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;Assert.Equal(GuardianProtocoloMmClient.Endpoint,request.RequestUri!.AbsoluteUri);Assert.Equal(HttpMethod.Post,request.Method);Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);Sent=await request.Content!.ReadAsByteArrayAsync(ct);return new(status){Content=new StringContent(body)};}}
    [Theory][InlineData(201,"{\"id\":1}",EstadoProtocoloMm.Criado)][InlineData(200,"{\"success\":true,\"audit\":{\"id\":73}}",EstadoProtocoloMm.Indeterminado)][InlineData(200,"{\"dados\":{\"id\":2}}",EstadoProtocoloMm.Criado)][InlineData(200,"{}",EstadoProtocoloMm.Indeterminado)][InlineData(200,"{\"id\":0}",EstadoProtocoloMm.Indeterminado)][InlineData(200,"{\"id\":2,\"success\":false}",EstadoProtocoloMm.Indeterminado)][InlineData(200,"{\"id\":2,\"audit\":{\"success\":false}}",EstadoProtocoloMm.Indeterminado)][InlineData(200,"{\"id\":2,\"data\":{\"id\":3}}",EstadoProtocoloMm.Indeterminado)][InlineData(400,"{\"error\":\"personal data must not escape\"}",EstadoProtocoloMm.Recusado)][InlineData(500,"internal",EstadoProtocoloMm.Indeterminado)]
    public async Task Wire_result_is_conservative_and_payload_exact(int status,string response,string state)
    {using var handler=new Transport((HttpStatusCode)status,response);using var http=new HttpClient(handler);var api=new GuardianProtocoloMmClient(http,_=>Task.FromResult("synthetic-test-token"),TimeSpan.FromSeconds(1));var bytes=Encoding.UTF8.GetBytes("{\"synthetic\":true}");var result=await api.EnviarAsync(bytes,default);Assert.Equal(state,result.Estado);Assert.Equal(status,result.Http);Assert.Equal(bytes,handler.Sent);Assert.Equal(1,handler.Calls);Assert.DoesNotContain("personal data",result.Mensagem);}
}
