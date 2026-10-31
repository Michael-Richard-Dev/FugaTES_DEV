using System.Text;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using Xunit;

public class CallbackContractTests
{
    internal const string Minimal="""{"ID":12345,"Sequencial":98765,"EstadoCodigo":5,"PesoBruto":"150.00","Tara":"50.00","PesoLiquido":"100.00"}""";
    static DefaultHttpContext Context(string body,Store store)
    {
        var c=new DefaultHttpContext();c.Request.ContentType="application/json";
        c.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes(body));c.Response.Body=new MemoryStream();
        c.RequestServices=new ServiceCollection().AddSingleton<IRegistroEventoGuardianStore>(store).AddScoped<ProcessarEventoGuardianCasoDeUso>().BuildServiceProvider();return c;
    }
    static string Body(DefaultHttpContext c){c.Response.Body.Position=0;return new StreamReader(c.Response.Body).ReadToEnd();}
    [Fact] public void Historical_route_is_preserved()=>Assert.Equal("/api/guardian/set26/callback/recebimento",GuardianCallbackEndpoint.Route);
    [Fact] public async Task Minimal_six_fields_are_accepted_after_store_commit_without_authorization_header()
    {
        var store=new Store();var c=Context(Minimal,store);await GuardianCallbackEndpoint.HandleEventoAsync(c);
        Assert.Equal(200,c.Response.StatusCode);Assert.Equal("OK",Body(c));Assert.Equal(1,store.Calls);Assert.Equal(98765,store.Last!.Sequencial);Assert.Equal(50m,store.Last.Tara);
    }
    [Fact] public async Task Duplicate_uses_same_fingerprint()
    {
        var store=new Store();await GuardianCallbackEndpoint.HandleEventoAsync(Context(Minimal,store));var first=store.Fingerprint;
        var c=Context(Minimal,store);await GuardianCallbackEndpoint.HandleEventoAsync(c);Assert.Equal(200,c.Response.StatusCode);Assert.Equal(first,store.Fingerprint);
    }
    [Fact] public async Task Storage_failure_cannot_return_success_ack()
    {
        var c=Context(Minimal,new Store{Failure=true});await GuardianCallbackEndpoint.HandleEventoAsync(c);Assert.Equal(500,c.Response.StatusCode);Assert.DoesNotContain("secret",Body(c));
    }
    [Fact] public async Task Conflicting_event_cannot_return_success_ack()
    {
        var c=Context(Minimal,new Store{Conflict=true});await GuardianCallbackEndpoint.HandleEventoAsync(c);Assert.Equal(409,c.Response.StatusCode);
    }
    [Fact] public async Task An_unattested_full_json_does_not_replace_the_minimal_wire()
    {
        var store=new Store();var c=Context("""{"IdPesagem":"98765","DataPesagem":"/Date(1)/","NomeMotorista":"SYNTHETIC"}""",store);
        await GuardianCallbackEndpoint.HandleEventoAsync(c);Assert.Equal(400,c.Response.StatusCode);Assert.Equal(0,store.Calls);
    }
    [Fact] public async Task Additional_unknown_fields_are_ignored_not_complete_persistence()
    {
        var store=new Store();var c=Context(Minimal.Replace("}",",\"NomeMotorista\":\"SYNTHETIC\",\"Qualidades\":[1]}"),store);
        await GuardianCallbackEndpoint.HandleEventoAsync(c);Assert.Equal(200,c.Response.StatusCode);
        Assert.Equal(6,typeof(EventoGuardianRecebimento).GetProperties().Length);
    }
    [Theory][InlineData(1)][InlineData(3)] public async Task Non_final_events_accept_absent_weights(int state)
    {
        var store=new Store();var c=Context($"{{\"ID\":12345,\"Sequencial\":98765,\"EstadoCodigo\":{state}}}",store);
        await GuardianCallbackEndpoint.HandleEventoAsync(c);Assert.Equal(200,c.Response.StatusCode);Assert.Null(store.Last!.PesoLiquido);
    }
    internal sealed class Store:IRegistroEventoGuardianStore
    {
        public int Calls;public EventoGuardianRecebimento? Last;public string? Fingerprint;public bool Failure,Conflict;
        public Task<StatusRegistroEventoGuardian> RegistrarAsync(EventoGuardianRecebimento e,string fp,CancellationToken ct)
        {Calls++;Last=e;Fingerprint=fp;if(Failure)throw new IOException("synthetic secret must not escape");return Task.FromResult(Conflict?StatusRegistroEventoGuardian.Conflito:Calls>1?StatusRegistroEventoGuardian.Idempotente:StatusRegistroEventoGuardian.Novo);}
    }
}
