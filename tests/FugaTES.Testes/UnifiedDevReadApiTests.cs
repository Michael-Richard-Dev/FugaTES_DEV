using System.Net;
using System.Net.Http.Json;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class UnifiedDevReadApiTests
{
    [Fact]
    public void Invalid_DEV_mode_arguments_do_not_fall_back_to_business_host_composition()
    {
        Assert.False(DevUnifiedReadApi.Selected([]));Assert.True(DevUnifiedReadApi.Selected(["--dev-unified-read-only"]));
        Assert.Throws<InvalidOperationException>(()=>DevUnifiedReadApi.Selected(["--dev-unified-read-only","extra"]));
    }
    [Fact]
    public async Task Private_api_rejects_missing_or_ambiguous_key_and_never_maps_business_routes()
    {
        var builder=DevUnifiedReadApi.CreateReadOnlyHostBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app=builder.Build();var store=new NoCalls();
        DevUnifiedReadApi.Map(app,store,new string('A',64));await app.StartAsync();
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync("/dev/read/identity")).StatusCode);Assert.Equal(0,store.Calls);
        http.DefaultRequestHeaders.Add("X-R165AL-Read-Key",new string('B',64));
        Assert.Equal(HttpStatusCode.Unauthorized,(await http.GetAsync("/dev/read/identity")).StatusCode);
        http.DefaultRequestHeaders.Remove("X-R165AL-Read-Key");http.DefaultRequestHeaders.Add("X-R165AL-Read-Key",new string('A',64));
        var identity=await http.GetFromJsonAsync<DevDatabaseIdentity>("/dev/read/identity");Assert.Equal(5433,identity!.Port);
        Assert.Equal(HttpStatusCode.NotFound,(await http.PostAsync("/api/v1/integracoes/sap/contratos",new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await http.PostAsync("/api/v1/acm/manual-retry/"+Guid.NewGuid(),new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,(await http.PostAsync("/dev/read/contracts",new StringContent("{}"))).StatusCode);
        Assert.Equal(1,store.Calls);await app.StopAsync();
    }

    [Theory]
    [InlineData(5432,"18/main","fugates_acm_hml_app")]
    [InlineData(5434,"18/fugatesmdhml","fugates_acm_hml_app")]
    [InlineData(5433,"18/fugatesacmdev","postgres")]
    [InlineData(5433,"18/fugatesacmdev","fugates_dev_app")]
    public void Wrong_database_identity_is_rejected(int port,string cluster,string role)
        =>Assert.Throws<InvalidOperationException>(()=>new DevDatabaseIdentity(cluster,port,"fugates_dev",role,true,DateTimeOffset.UtcNow).Validate());

    static DevDatabaseIdentity Identidade(string role)
        =>new("18/fugatesacmdev",5433,"fugates_dev",role,true,DateTimeOffset.UtcNow);

    // R166: o cliente (DEV110) recebe a identidade pela API e nao sabe qual das duas identidades de
    // leitura o servidor configurou, logo aceita as duas — e somente essas duas.
    [Theory]
    [InlineData("fugates_acm_hml_app")]
    [InlineData("fugates_dev_read_app")]
    public void Both_authorized_read_identities_are_accepted_by_the_client(string role)
        =>Identidade(role).Validate();

    // A identidade de ESCRITA do receptor SAP nunca pode servir leitura, nem no cliente.
    [Fact]
    public void The_sap_writer_identity_is_never_accepted_for_reading()
        =>Assert.Throws<InvalidOperationException>(()=>Identidade("fugates_sap_inbound_app").Validate());

    // O servidor e mais estrito que o cliente: exige exatamente a role que configurou.
    [Fact]
    public void The_server_requires_exactly_the_configured_identity()
    {
        Identidade("fugates_dev_read_app").Validate("fugates_dev_read_app");
        Assert.Throws<InvalidOperationException>(()=>Identidade("fugates_acm_hml_app").Validate("fugates_dev_read_app"));
        Assert.Throws<InvalidOperationException>(()=>Identidade("fugates_dev_read_app").Validate("fugates_acm_hml_app"));
    }

    // Aceitar a segunda role NAO afrouxou nenhuma das outras condicoes fisicas.
    [Theory]
    [InlineData(5432,"18/fugatesacmdev","fugates_dev",true)]
    [InlineData(5433,"18/main","fugates_dev",true)]
    [InlineData(5433,"18/fugatesacmdev","fugates_hml",true)]
    [InlineData(5433,"18/fugatesacmdev","fugates_dev",false)]
    public void The_new_read_identity_still_requires_every_physical_condition(int port,string cluster,string database,bool readOnly)
        =>Assert.Throws<InvalidOperationException>(()=>
            new DevDatabaseIdentity(cluster,port,database,"fugates_dev_read_app",readOnly,DateTimeOffset.UtcNow).Validate());

    [Fact]
    public void Scope_never_treats_a_persisted_projection_as_original_SAP_receipt()
    {
        Assert.Equal("SIMULACAO_CONFIRMADA",DevReadScope.Classify(DevReadScope.PilotId,"0000986801"));
        Assert.Equal("PROJECAO_PERSISTIDA_ORIGEM_NAO_ATESTADA",DevReadScope.Classify(Guid.NewGuid(),"0000001140"));
    }
    sealed class NoCalls:IDevUnifiedReadStore
    {
        public int Calls;
        public Task<DevDatabaseIdentity> IdentityAsync(CancellationToken ct){Calls++;return Task.FromResult(new DevDatabaseIdentity("18/fugatesacmdev",5433,"fugates_dev","fugates_acm_hml_app",true,DateTimeOffset.UtcNow));}
        public Task<object> ReadAsync(string resource,string? selector,CancellationToken ct)=>throw new InvalidOperationException("UNEXPECTED_QUERY");
    }
}
