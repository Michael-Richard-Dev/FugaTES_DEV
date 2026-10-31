using System.Net;
using System.Text;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class ToledoPrivateDevAdmissionTests
{
    static Dictionary<string,string?> Config()=>new()
    {
        ["ToledoInbound:Authentication:Mode"]="PrivateDevNetworkOnly",
        ["ToledoInbound:Authentication:Environment"]="DEV",
        ["ToledoInbound:Authentication:SourceIdentity"]="PRIVATE_NGINX_ALLOWLIST_DEV",
        ["ToledoInbound:Authentication:AllowedRemoteAddresses:0"]="127.0.0.1",
        ["ToledoInbound:Authentication:PrivateLoopbackProxyApproved"]="true",
        ["ToledoInbound:Authentication:PrivateTransportVerified"]="true",
        ["ToledoInbound:Authentication:SourceVerified"]="true",
        ["ToledoInbound:Authentication:ApprovalReference"]="USER_TOLEDO_NO_KEY_20261028",
        ["ToledoInbound:Authentication:PrivateListenerPort"]="18086"
    };
    static DefaultHttpContext Context()
    {
        var c=new DefaultHttpContext();c.Request.Scheme="http";
        c.Connection.LocalIpAddress=IPAddress.Loopback;c.Connection.RemoteIpAddress=IPAddress.Loopback;c.Connection.LocalPort=18086;
        return c;
    }
    static ProtectedToledoCallbackAdmission Admission(Dictionary<string,string?>? values=null)=>new(
        ToledoCallbackAdmissionOptions.Parse(new ConfigurationBuilder().AddInMemoryCollection(values??Config()).Build()),
        _=>throw new InvalidOperationException("NO_SECRET_READ_ALLOWED_IN_NETWORK_MODE"));
    [Fact] public async Task Explicit_private_DEV_mode_accepts_without_any_header_or_secret_file()
    {
        var actor=await Admission().AuthorizeAsync(Context(),default);
        Assert.True(actor.Accepted);Assert.Equal("PRIVATE_NGINX_ALLOWLIST_DEV",actor.SourceIdentity);
        Assert.Equal("TOLEDO_PRIVATE_NETWORK_ADMITTED",actor.Code);
    }
    [Theory]
    [InlineData("Environment","Production")][InlineData("Environment","HML")]
    [InlineData("PrivateLoopbackProxyApproved","false")][InlineData("PrivateTransportVerified","false")]
    [InlineData("SourceVerified","false")][InlineData("ApprovalReference","")]
    [InlineData("Mode","Unknown")][InlineData("PrivateListenerPort","8080")]
    [InlineData("PrivateListenerPort","")][InlineData("AllowedRemoteAddresses:0","192.0.2.1")]
    public async Task Invalid_or_unapproved_network_mode_is_fail_closed(string key,string value)
    {
        var config=Config();config["ToledoInbound:Authentication:"+key]=value;
        Assert.False((await Admission(config).AuthorizeAsync(Context(),default)).Accepted);
    }
    [Theory][InlineData("peer")][InlineData("local")][InlineData("port")][InlineData("forged-forwarded")]
    public async Task Wrong_socket_identity_cannot_be_overridden_by_forwarded_headers(string variant)
    {
        var c=Context();c.Request.Headers["X-Forwarded-For"]="192.168.17.91";c.Request.Headers["X-Forwarded-Proto"]="https";
        c.Request.Headers["X-FugaTES-Toledo-Auth"]=new string('X',64);
        if(variant is "peer" or "forged-forwarded")c.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.1");
        if(variant=="local")c.Connection.LocalIpAddress=IPAddress.Any;
        if(variant=="port")c.Connection.LocalPort=8080;
        Assert.False((await Admission().AuthorizeAsync(c,default)).Accepted);
    }
    [Fact] public async Task Default_mode_still_demands_the_existing_key()
    {
        var config=Config();config.Remove("ToledoInbound:Authentication:Mode");
        config["ToledoInbound:Authentication:HeaderName"]="X-FugaTES-Toledo-Auth";
        config["ToledoInbound:Authentication:SecretFile"]=Path.Combine(Path.GetTempPath(),"SYNTHETIC_NO_SECRET_READ");
        var actor=await Admission(config).AuthorizeAsync(Context(),default);
        Assert.False(actor.Accepted);Assert.Equal(401,actor.HttpStatus);
    }
    [Fact] public async Task Network_admission_keeps_full_endpoint_persistence_before_200()
    {
        var store=new ReceiptStore();var services=new ServiceCollection();
        services.AddSingleton<IToledoCallbackAdmission>(Admission());services.AddSingleton<IRegistroPesagemToledoStore>(store);
        services.AddScoped<RegistrarPesagemToledoCasoDeUso>();using var sp=services.BuildServiceProvider();
        var c=Context();c.RequestServices=sp;c.Request.Method="POST";c.Request.ContentType="application/json";
        c.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes(ToledoCallbackTests.ValidJson));c.Response.Body=new MemoryStream();
        await GuardianCallbackEndpoint.HandleDualAsync(c);
        Assert.Equal(200,c.Response.StatusCode);Assert.Equal(1,store.Calls);Assert.True(store.Committed);
        Assert.Equal("PRIVATE_NGINX_ALLOWLIST_DEV",store.Source);
    }
    sealed class ReceiptStore:IRegistroPesagemToledoStore
    {
        public int Calls;public bool Committed;public string? Source;
        public async Task<ReciboPesagemToledo> RegistrarAsync(PesagemToledoRecebida doc,string fingerprint,CancellationToken ct)
        {
            Calls++;Source=doc.OrigemAutenticada;await Task.Yield();Committed=true;
            return new(Guid.NewGuid(),DateTimeOffset.UtcNow,false);
        }
    }
}
