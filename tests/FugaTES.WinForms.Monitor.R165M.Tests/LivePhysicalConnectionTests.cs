using System.Net;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.WinForms.Monitor;
using R165AL;
using R165AL.Desktop;

public sealed class LivePhysicalConnectionTests
{
    static PhysicalFrame Frame(PilotPlan p,Guid id)=>new(){RunId=p.RunId,IntegrationId=id,Contract=p.Contract,Description=p.Description,
        Fingerprint=new string('A',64),H4Version=1,CorrelationId="r165ag:cpi:"+p.RunId.ToString("N")+":"+Guid.NewGuid().ToString("N"),
        Cluster="18/fugatesacmdev",Port=5433,Database="fugates_dev",Role="fugates_acm_hml_app",ReadOnly=true,
        CapturedUtc=DateTimeOffset.UtcNow,CreatedUtc=DateTimeOffset.UtcNow,State="CONCLUIDA",Stage="R165U_CONFIRMED",
        Protocol="986801",H4Protocol="986801",Attempts=1,Fencing=1,ConfirmedCode=p.Contract,Reference=p.Reference,RestartVerified=true};

    [Fact]
    public async Task Connection_is_reported_only_after_a_fresh_physical_identity_is_validated()
    {
        var plan=PilotPlan.Create();var id=Guid.NewGuid();var prepared=0;var wire=new Wire(Frame(plan,id));
        using var client=new PhysicalMonitorClient(new("http://127.0.0.1:18761"),"TEST_PRIVATE_KEY",plan,id,wire,
            _=>{prepared++;return Task.CompletedTask;});
        Assert.False(client.Connection.Connected);
        var rows=await client.ConsultarAsync(new(),default);
        Assert.Single(rows.Integracoes);Assert.Equal(1,prepared);Assert.True(client.Connection.Connected);
        Assert.Equal(5433,client.Connection.ServerPort);Assert.Equal("fugates_dev",client.Connection.Database);
        Assert.Equal("fugates_acm_hml_app",client.Connection.Role);Assert.NotNull(client.Connection.LastSuccessUtc);
        Assert.Contains("CONECTADO",client.Connection.Display);Assert.DoesNotContain("TEST_PRIVATE_KEY",client.Connection.Display);
    }

    [Theory]
    [InlineData("http")][InlineData("identity")][InlineData("network")]
    public async Task Loss_of_connection_clears_connected_state_and_never_returns_cached_rows(string failure)
    {
        var plan=PilotPlan.Create();var id=Guid.NewGuid();var wire=new Wire(Frame(plan,id));
        using var client=new PhysicalMonitorClient(new("http://127.0.0.1:18761"),"TEST_PRIVATE_KEY",plan,id,wire);
        await client.ConsultarAsync(new(),default);var last=client.Connection.LastSuccessUtc;
        if(failure=="http")wire.Status=HttpStatusCode.Unauthorized;
        if(failure=="identity")wire.Frame=wire.Frame with {Port=5432};
        if(failure=="network")wire.Unavailable=true;
        await Assert.ThrowsAnyAsync<Exception>(()=>client.ConsultarAsync(new(),default));
        Assert.False(client.Connection.Connected);Assert.Equal(last,client.Connection.LastSuccessUtc);
        Assert.Contains("DESCONECTADO",client.Connection.Display);Assert.NotEmpty(client.Connection.Message);
        wire.Status=HttpStatusCode.OK;wire.Unavailable=false;wire.Frame=Frame(plan,id);
        Assert.Single((await client.ConsultarAsync(new(),default)).Integracoes);Assert.True(client.Connection.Connected);
    }

    [Fact]
    public async Task Tunnel_rejection_prevents_sending_the_private_API_key()
    {
        var plan=PilotPlan.Create();var id=Guid.NewGuid();var wire=new Wire(Frame(plan,id));
        using var client=new PhysicalMonitorClient(new("http://127.0.0.1:18761"),"TEST_PRIVATE_KEY",plan,id,wire,
            _=>throw new PhysicalConnectionException("LOCAL_PORT_UNTRUSTED"));
        await Assert.ThrowsAsync<PhysicalConnectionException>(()=>client.ConsultarAsync(new(),default));
        Assert.Equal(0,wire.Requests);Assert.False(client.Connection.Connected);
    }

    [Fact]
    public void Tunnel_contract_is_read_only_pinned_and_rejects_other_endpoints_or_extra_commands()
    {
        var options=Options();var args=LocalReadTunnel.BuildArguments(options);
        Assert.Contains("-N",args);Assert.Contains("StrictHostKeyChecking=yes",args);
        Assert.Contains("UpdateHostKeys=no",args);Assert.Contains("BatchMode=yes",args);
        Assert.Contains("127.0.0.1:18761:127.0.0.1:18761",args);
        Assert.True(LocalReadTunnel.IsApprovedCommand(LocalReadTunnel.SshExecutable,args,options));
        Assert.False(LocalReadTunnel.IsApprovedCommand(LocalReadTunnel.SshExecutable,args.Concat(["id"]).ToArray(),options));
        Assert.False(LocalReadTunnel.IsApprovedCommand("C:\\untrusted\\ssh.exe",args,options));
        Assert.Throws<PhysicalConnectionException>(()=>LocalReadTunnel.BuildArguments(options with {Host="192.168.17.91"}));
        Assert.Throws<PhysicalConnectionException>(()=>LocalReadTunnel.BuildArguments(options with {User="postgres"}));
        Assert.DoesNotContain(args,a=>a.Contains("READ_API_KEY"));
    }

    [Fact]
    public void Main_shell_has_an_explicit_disconnected_status_without_enabling_business_modules()
    {
        FugaTES.Testes.GuardianProtocolFormTests.OnSta(()=>
        {
            using var session=new UnifiedDesktopSession(new(),Path.GetTempPath());
            using var form=new FugaTES.WinForms.PainelInicialForm(unified:session);
            var status=FugaTES.Testes.GuardianProtocolFormTests.Descendants(form).Single(c=>c.Name=="PhysicalConnectionStatus");
            Assert.Contains("DESCONECTADO",status.Text);
        });
    }
    static LocalReadTunnelOptions Options()=>new(){Enabled=true,Host="192.168.17.40",User="michael",
        IdentityFile="C:\\approved\\identity",KnownHostsFile="C:\\approved\\known_hosts"};
    sealed class Wire(PhysicalFrame frame):HttpMessageHandler
    {
        public PhysicalFrame Frame=frame;public HttpStatusCode Status=HttpStatusCode.OK;public bool Unavailable;public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Requests++;Assert.Equal(HttpMethod.Get,request.Method);Assert.Equal("127.0.0.1",request.RequestUri!.Host);
            if(Unavailable)throw new HttpRequestException("Synthetic network failure");
            return Task.FromResult(new HttpResponseMessage(Status){Content=new StringContent(JsonSerializer.Serialize(Frame),Encoding.UTF8,"application/json")});
        }
    }
}
