using System.Net;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Infraestrutura.ManualRetry;
using FugaTES.WinForms.ManualRetry;

public sealed class RetryCommandDesktopTests
{
    const string Sid="S-1-5-21-1-2-3-1001";
    static readonly DateTimeOffset Now=new(2026,10,28,12,0,0,TimeSpan.Zero);
    static ContratoAcmLeitura Row()=>new("3011","OFFLINE-CONTRACT",1,Now.UtcDateTime,"T","FALHA_DEFINITIVA",Guid.Parse("e36890c7-1302-489a-990e-1c92c3803d93"),"OFFLINE",true){Item="10"};
    static RetryCommandDesktopProfile Profile()=>new(){Enabled=true,Homologated=true,ApprovalReference="OFFLINE_ONLY",OperatorSid=Sid,UntilUtc=Now.AddMinutes(1),CommandCredentialFile=@"C:\Private\retry-command.key",Tunnel=new(){Enabled=true,Host="192.168.17.40",User="michael",IdentityFile=@"C:\Private\ssh.key",KnownHostsFile=@"C:\Private\known_hosts"}};
    static AcmRetryCommandBinding Binding()=>new(new("http://127.0.0.1:18763"),true,"OFFLINE_ONLY",Sid,Now.AddMinutes(1));
    sealed class FixedTime:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
    sealed class MemoryTransport(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(respond(request));}
    static HttpResponseMessage Preview(AcmRetryPreview value)=>new(HttpStatusCode.OK){Content=JsonContent.Create(value)};

    [Fact] public async Task Denied_preview_without_disclosed_contract_preserves_server_blocker_after_tunnel_prepare()
    {
        var row=Row();var prepared=false;
        using var client=new AuthenticatedAcmRetryCommandClient(Binding(),new string('X',40),new FixedTime(),new MemoryTransport(request=>
        {Assert.True(prepared);Assert.Equal(HttpMethod.Get,request.Method);Assert.Equal("/api/v1/acm/manual-retry/e36890c7-1302-489a-990e-1c92c3803d93/preview",request.RequestUri!.AbsolutePath);return Preview(new(row.IntegracaoId,"",null,"BLOCKED",null,"",new(false,"COMMAND_NOT_AUTHORIZED","Unit approval absent."),Sid));}),ct=>{prepared=true;return Task.CompletedTask;});
        var result=await client.PreviewAsync(row,default);
        Assert.False(result.Decision.Eligible);Assert.Equal("COMMAND_NOT_AUTHORIZED",result.Decision.Code);
    }
    [Theory][InlineData(true,false,false)][InlineData(false,true,false)][InlineData(false,false,true)]
    public async Task Preview_never_accepts_wrong_identity_or_an_eligible_response_without_contract(bool wrongId,bool wrongSid,bool eligible)
    {
        var row=Row();
        using var client=new AuthenticatedAcmRetryCommandClient(Binding(),new string('X',40),new FixedTime(),new MemoryTransport(_=>Preview(new(wrongId?Guid.Empty:row.IntegracaoId,"",null,"BLOCKED",null,"",new(eligible,"OFFLINE","Offline only"),wrongSid?"S-1-5-21-7-8-9-1001":Sid))));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.PreviewAsync(row,default));
    }
    [Fact]public async Task Failed_tunnel_preparation_never_sends_or_retries_a_command()
    {
        var calls=0;var prepares=0;
        using var client=new AuthenticatedAcmRetryCommandClient(Binding(),new string('X',40),new FixedTime(),new MemoryTransport(_=>{calls++;throw new Exception("Must not send");}),ct=>{prepares++;throw new InvalidOperationException("OFFLINE_PREPARE_FAILED");});
        var result=await client.SubmitAsync(new(Guid.NewGuid(),Row().IntegracaoId,new string('A',64),"Offline justification only",true),default);
        Assert.Equal("UNKNOWN",result.State);Assert.Equal(0,calls);Assert.Equal(1,prepares);
    }
    [Fact]public async Task Prepared_command_uses_one_dedicated_route_and_verifies_receipt()
    {
        var prepared=false;var calls=0;var id=Guid.NewGuid();var row=Row();
        using var client=new AuthenticatedAcmRetryCommandClient(Binding(),new string('X',40),new FixedTime(),new MemoryTransport(request=>
        {Assert.True(prepared);calls++;Assert.Equal(HttpMethod.Post,request.Method);Assert.Equal("/api/v1/acm/manual-retry/e36890c7-1302-489a-990e-1c92c3803d93",request.RequestUri!.AbsolutePath);return new(HttpStatusCode.Accepted){Content=JsonContent.Create(new AcmRetryAdmission("QUEUED","OFFLINE",id,row.IntegracaoId))};}),ct=>{prepared=true;return Task.CompletedTask;});
        Assert.True((await client.SubmitAsync(new(id,row.IntegracaoId,new string('A',64),"Offline justification only",true),default)).Accepted);Assert.Equal(1,calls);
    }
    [Fact]public void Profile_requires_current_sid_expiry_approval_and_dedicated_credential_path()
    {
        var valid=Profile();Assert.Equal(18763,valid.Validate(Sid,Now,@"C:\App",@"C:\Private\read.key").Endpoint.Port);
        foreach(var bad in new[]{valid with{Enabled=false},valid with{Homologated=false},valid with{OperatorSid="S-1-5-21-7-8-9-1001"},valid with{UntilUtc=Now},valid with{ApprovalReference=""},valid with{CommandCredentialFile=@"C:\App\retry.key"},valid with{CommandCredentialFile=@"C:\Private\read.key"},valid with{CommandCredentialFile=@"C:\Private\ssh.key"},valid with{CommandCredentialFile=@"\\server\share\retry.key"},valid with{CommandCredentialFile=@"C:\Private\read.key:retry"},valid with{UntilUtc=Now.ToOffset(TimeSpan.FromHours(-3)).AddMinutes(1)}})
            Assert.Throws<InvalidOperationException>(()=>bad.Validate(Sid,Now,@"C:\App",@"C:\Private\read.key"));
    }
    [Theory][InlineData(18761)][InlineData(18762)][InlineData(5433)]
    public void Dedicated_tunnel_rejects_all_other_ports(int port)
        =>Assert.Throws<InvalidOperationException>(()=>LocalRetryCommandTunnel.BuildArguments(Profile().Tunnel! with{RemoteCommandPort=port}));
    [Fact]public void Dedicated_tunnel_is_exactly_loopback_18763_with_strict_host_verification()
    {
        var options=Profile().Tunnel!;var args=LocalRetryCommandTunnel.BuildArguments(options);
        Assert.Contains("127.0.0.1:18763:127.0.0.1:18763",args);Assert.Contains("StrictHostKeyChecking=yes",args);Assert.Contains("UpdateHostKeys=no",args);Assert.Equal("michael@192.168.17.40",args[^1]);
        Assert.True(LocalRetryCommandTunnel.IsApprovedCommand(LocalRetryCommandTunnel.SshExecutable,args,options));
        Assert.False(LocalRetryCommandTunnel.IsApprovedCommand(LocalRetryCommandTunnel.SshExecutable,args.Append("whoami").ToArray(),options));
        Assert.Throws<InvalidOperationException>(()=>LocalRetryCommandTunnel.BuildArguments(options with{Host="other"}));
        Assert.Throws<InvalidOperationException>(()=>LocalRetryCommandTunnel.BuildArguments(options with{User="root"}));
    }
    [Fact]public void Credential_acl_rejects_inheritance_foreign_accounts_and_missing_operator_access()
    {
        FileSecurity Acl(bool protect,string account){var acl=new FileSecurity();acl.SetAccessRuleProtection(protect,false);acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(account),FileSystemRights.Read,AccessControlType.Allow));return acl;}
        RetryCommandCredential.ValidateAcl(Acl(true,Sid),Sid);
        Assert.Throws<InvalidOperationException>(()=>RetryCommandCredential.ValidateAcl(Acl(false,Sid),Sid));
        Assert.Throws<InvalidOperationException>(()=>RetryCommandCredential.ValidateAcl(Acl(true,"S-1-1-0"),Sid));
        Assert.Throws<InvalidOperationException>(()=>RetryCommandCredential.ValidateAcl(Acl(true,"S-1-5-18"),Sid));
    }
    [Fact]public async Task Expiry_during_tunnel_preparation_prevents_any_command_request()
    {
        var time=new MutableTime();var requests=0;
        using var client=new AuthenticatedAcmRetryCommandClient(Binding(),new string('X',40),time,new MemoryTransport(_=>{requests++;throw new Exception("Must not send");}),ct=>{time.Now=Now.AddMinutes(2);return Task.CompletedTask;});
        Assert.Equal("BLOCKED",(await client.SubmitAsync(new(Guid.NewGuid(),Row().IntegracaoId,new string('A',64),"Offline justification only",true),default)).State);Assert.Equal(0,requests);
    }
    sealed class MutableTime:TimeProvider{public DateTimeOffset Now=RetryCommandDesktopTests.Now;public override DateTimeOffset GetUtcNow()=>Now;}
    [Fact]public async Task Eligible_preview_requires_exact_contract_item_and_digest()
    {
        var row=Row();
        var complete=new AcmRetryPreview(row.IntegracaoId,row.NumeroContrato,row.Item,row.EstadoIntegracao,"R165U_SOAP_CONFIRMED",new string('A',64),new(true,"REST_ONLY","Offline fixture",AcmRetryStage.RestOnly),Sid);
        foreach(var bad in new[]{complete with{Contract="OTHER"},complete with{Item=null},complete with{Item="20"},complete with{SnapshotDigest=""}})
        {
            using var client=new AuthenticatedAcmRetryCommandClient(Binding(),new string('X',40),new FixedTime(),new MemoryTransport(_=>Preview(bad)));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>client.PreviewAsync(row,default));
        }
        using var valid=new AuthenticatedAcmRetryCommandClient(Binding(),new string('X',40),new FixedTime(),new MemoryTransport(_=>Preview(complete)));
        Assert.True((await valid.PreviewAsync(row,default)).Decision.Eligible);
    }
    [Fact]public async Task Disposed_tunnel_cannot_probe_or_start_any_process()
    {
        using var tunnel=new LocalRetryCommandTunnel(Profile().Tunnel!);tunnel.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>tunnel.EnsureAsync(default));
    }
    [Fact]public async Task Disposing_pending_tunnel_preparation_never_requires_the_UI_context_to_resume()
    {
        var context=new QueuedContext();
        var finished=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui=new Thread(()=>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                using var tunnel=new LocalRetryCommandTunnel(Profile().Tunnel!,async ct=>
                {
                    await Task.Delay(Timeout.Infinite,ct).ConfigureAwait(false);
                    return null;
                });
                var preparation=tunnel.EnsureAsync(default);
                tunnel.Dispose();
                var canceled=false;
                try{preparation.GetAwaiter().GetResult();}
                catch(OperationCanceledException){canceled=true;}
                finished.TrySetResult(canceled);
            }
            catch(Exception failure){finished.TrySetException(failure);}
        }){IsBackground=true};
        ui.Start();
        var returnedWithoutPumping=await Task.WhenAny(finished.Task,Task.Delay(TimeSpan.FromSeconds(2)))==finished.Task;
        // Release an intentionally reproduced old deadlock so a RED assertion cannot strand a test thread.
        var cleanup=System.Diagnostics.Stopwatch.StartNew();
        while(!finished.Task.IsCompleted&&cleanup.Elapsed<TimeSpan.FromSeconds(2))
        {context.Drain();await Task.Delay(10);}
        Assert.True(returnedWithoutPumping,"Dispose depended on a continuation queued to the blocked UI context.");
        Assert.True(await finished.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(ui.Join(TimeSpan.FromSeconds(2)));
    }
    sealed class QueuedContext:SynchronizationContext
    {
        readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback,object? State)> queue=new();
        public override void Post(SendOrPostCallback callback,object? state)=>queue.Enqueue((callback,state));
        public void Drain(){while(queue.TryDequeue(out var work))work.Callback(work.State);}
    }
    [Fact]public void Credential_path_rejects_git_checkouts_without_opening_a_secret_file()
    {
        var directory=Path.Combine(Path.GetTempPath(),"fugates-path-only-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory,".git"));
        try{Assert.Throws<InvalidOperationException>(()=>RetryCommandCredential.ValidatePath(Path.Combine(directory,"never-created-command.key"),@"C:\App"));}
        finally{Directory.Delete(directory,true);}
    }
    [Fact]public void Absent_or_disabled_profile_keeps_session_binding_readonly_without_credential_or_tunnel()
    {
        var directory=Path.Combine(Path.GetTempPath(),"fugates-offline-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            using var absent=RetryCommandDesktopBinding.Load(directory,@"C:\Private\read.key");Assert.IsType<ReadOnlyAcmManualRetryClient>(absent.Client);Assert.False(absent.Client.Configured);
            File.WriteAllText(Path.Combine(directory,"FugaTES.retry-command.json"),"{\"Enabled\":false,\"CommandCredentialFile\":\"MustNeverBeOpened\"}");
            using var disabled=RetryCommandDesktopBinding.Load(directory,@"C:\Private\read.key");Assert.IsType<ReadOnlyAcmManualRetryClient>(disabled.Client);Assert.False(disabled.Client.Configured);
        }
        finally{Directory.Delete(directory,true);}
    }
}


