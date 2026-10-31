using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.WinForms.ManualRetry;
using FugaTES.WinForms.Guardian;
using FugaTES.Testes;
using System.Windows.Forms;

public sealed class ProtocolRestRetryTests
{
    const string Reason="Recuperação REST com causa corrigida e evidência aprovada";
    [Fact] public async Task Rest_retry_uses_existing_integration_and_command_and_auditable_reason()
    {
        var f=new Fixture();var p=await f.Controller.InspectAsync(f.Row,default);
        Assert.True(p.Command.Decision.Eligible);Assert.Equal(AcmRetryStage.RestOnly,p.Command.Decision.Stage);
        var result=await f.Controller.SubmitAsync(p,Reason,true,default);
        Assert.True(result.Accepted);var request=Assert.Single(f.Client.Requests);
        Assert.Equal(f.Row.IntegracaoId,request.IntegrationId);Assert.Equal(Reason,request.Justification);
        Assert.Equal(f.Client.Digest,request.SnapshotDigest);Assert.True(request.Confirm);
        Assert.Equal(f.Contract.Item,p.Contract!.Item);Assert.Null(f.Row.Item); // H4 supplies the real item; projection stays intact.
    }
    [Theory] [InlineData("success")] [InlineData("unknown")] [InlineData("synthetic")]
    [InlineData("busy")] [InlineData("correlation")] [InlineData("no-reference")] [InlineData("no-contract")]
    public async Task Unsafe_physical_projection_never_sends(string condition)
    {
        var f=new Fixture();f.Rows=[condition switch {
            "success"=>f.Row with{Protocolo="123"},"unknown"=>f.Row with{ResultadoIndeterminado=true},
            "synthetic"=>f.Row with{Origem=ProtocoloPreCadastroLinha.Simulada},
            "busy"=>f.Row with{EstadoMotor="EM_PROCESSAMENTO",ProcessamentoAtivo=true},
            "correlation"=>f.Row with{CorrelacaoAmbigua=true},"no-reference"=>f.Row with{Referencia=null},
            _=>f.Row with{ContratoConfirmado=false}}];
        var preview=await f.Controller.InspectAsync(f.Rows.Single(),default);
        Assert.False(preview.Command.Decision.Eligible);
        Assert.False((await f.Controller.SubmitAsync(preview,Reason,true,default)).Accepted);Assert.Empty(f.Client.Requests);
    }
    [Theory] [InlineData("contract")] [InlineData("correlation")] [InlineData("item")] [InlineData("missing")] [InlineData("duplicate")]
    public async Task H4_must_match_exactly_one_original_integration(string change)
    {
        var f=new Fixture();f.Contracts=change switch {
            "contract"=>[f.Contract with{NumeroContrato="OTHER"}],"correlation"=>[f.Contract with{CorrelationId="OTHER"}],
            "item"=>[f.Contract with{Item=null}],"missing"=>[],_=>[f.Contract,f.Contract]};
        var p=await f.Controller.InspectAsync(f.Row,default);Assert.False(p.Command.Decision.Eligible);Assert.Empty(f.Client.Requests);
    }
    [Theory] [InlineData("http400")] [InlineData("origin")] [InlineData("approval")]
    public async Task Server_rejection_cannot_be_promoted_by_the_monitor(string code)
    {
        var f=new Fixture();f.Client.Decision=new(false,code,"Proof missing");
        var p=await f.Controller.InspectAsync(f.Row,default);Assert.False(p.Command.Decision.Eligible);
        Assert.False((await f.Controller.SubmitAsync(p,Reason,true,default)).Accepted);Assert.Empty(f.Client.Requests);
    }
    [Fact] public async Task Soap_stage_is_rejected_even_when_server_preview_is_eligible()
    {
        var f=new Fixture();f.Client.Decision=new(true,"PRE_DISPATCH","SOAP is forbidden here",AcmRetryStage.SoapThenRest);
        var p=await f.Controller.InspectAsync(f.Row,default);Assert.False(p.Command.Decision.Eligible);
        Assert.False((await f.Controller.SubmitAsync(p,Reason,true,default)).Accepted);Assert.Empty(f.Client.Requests);
    }
    [Theory] [InlineData("reference")] [InlineData("digest")] [InlineData("stage")] [InlineData("final-stage")]
    public async Task Changed_preview_or_reference_cannot_change_the_authorized_stage(string change)
    {
        var f=new Fixture();var p=await f.Controller.InspectAsync(f.Row,default);
        if(change=="reference")f.Rows=[f.Row with{Referencia="OTHER"}];
        if(change=="digest")f.Client.Digest=new string('B',64);
        if(change=="stage")f.Client.Decision=new(true,"SOAP","Wrong stage",AcmRetryStage.SoapThenRest);
        if(change=="final-stage")f.Client.SoapOnPreview=3; // inner controller revalidates immediately before submit.
        Assert.False((await f.Controller.SubmitAsync(p,Reason,true,default)).Accepted);Assert.Empty(f.Client.Requests);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Unknown_and_queued_results_prevent_second_command(bool unknown)
    {
        var f=new Fixture();f.Client.Unknown=unknown;var p=await f.Controller.InspectAsync(f.Row,default);
        var first=await f.Controller.SubmitAsync(p,Reason,true,default);
        Assert.Equal(unknown?"UNKNOWN":"QUEUED",first.State);
        Assert.False((await f.Controller.SubmitAsync(p,Reason,true,default)).Accepted);Assert.Single(f.Client.Requests);
    }
    [Fact] public async Task Both_monitors_share_the_same_concurrency_latch()
    {
        var f=new Fixture();f.Client.Hold=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var p=await f.Controller.InspectAsync(f.Row,default);
        var sending=f.Controller.SubmitAsync(p,Reason,true,default);await f.Client.Entered.Task;
        var second=await f.Shared.SubmitAsync(f.Contract,p.Command,Reason,true,default);
        Assert.False(second.Accepted);f.Client.Hold.TrySetResult();Assert.True((await sending).Accepted);Assert.Single(f.Client.Requests);
    }
    [Fact] public async Task Confirmation_and_justification_are_required()
    {
        var f=new Fixture();var p=await f.Controller.InspectAsync(f.Row,default);
        Assert.False((await f.Controller.SubmitAsync(p,Reason,false,default)).Accepted);
        Assert.False((await f.Controller.SubmitAsync(p,"",true,default)).Accepted);Assert.Empty(f.Client.Requests);
    }
    [Fact] public async Task Read_error_is_blocked_and_never_exposes_raw_exception()
    {
        var f=new Fixture();f.ThrowRead=true;var p=await f.Controller.InspectAsync(f.Row,default);
        Assert.False(p.Command.Decision.Eligible);Assert.DoesNotContain("private-token",p.Command.Decision.Message);Assert.Empty(f.Client.Requests);
    }
    [Fact] public void Partial_extension_wires_real_button_preserving_JSON_and_Claude_constructor()
    {
        GuardianProtocolFormTests.OnSta(()=>{
            var f=new Fixture();using var form=new FrmProtocolosPreCadastro(new(f));
            form.ConfigureRestRetry(f.Controller);
            var grid=GuardianProtocolFormTests.Descendants(form).OfType<DataGridView>().Single();
            Assert.NotNull(grid.Columns["json"]);Assert.NotNull(grid.Columns["restRetryCommand"]);
            Assert.Null(grid.Columns["reenviar"]); // legacy information-only click cannot intercept this button.
            Assert.Equal("Reenviar",((DataGridViewButtonColumn)grid.Columns["restRetryCommand"]!).Text);
            Assert.Throws<InvalidOperationException>(()=>form.ConfigureRestRetry(f.Controller));
        });
    }
    [Fact] public async Task Authenticated_transport_is_shared_and_posts_only_the_command_not_SOAP_or_Guardian()
    {
        var f=new Fixture();var methods=new List<string>();
        using var client=new FugaTES.Infraestrutura.ManualRetry.AuthenticatedAcmRetryCommandClient(
            new(new Uri("http://127.0.0.1:18763"),true,"OFFLINE_APPROVAL","S-1-5-21-1-2-3-1001",DateTimeOffset.UtcNow.AddMinutes(5)),
            new string('x',64),TimeProvider.System,new Transport(request=>{
                Assert.Equal("127.0.0.1",request.RequestUri!.Host);Assert.Equal(18763,request.RequestUri.Port);
                Assert.StartsWith("/api/v1/acm/manual-retry/",request.RequestUri.AbsolutePath);
                Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);methods.Add(request.Method.Method);
                if(request.Method==HttpMethod.Get)return System.Net.Http.Json.JsonContent.Create(new AcmRetryPreview(f.Row.IntegracaoId,f.Contract.NumeroContrato,f.Contract.Item,f.Contract.EstadoIntegracao,
                    "R165U_SOAP_CONFIRMED",new string('A',64),new(true,"REST_ONLY","Server approval",AcmRetryStage.RestOnly),"S-1-5-21-1-2-3-1001"));
                var command=System.Text.Json.JsonSerializer.Deserialize<AcmRetryRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(),new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
                Assert.Equal(f.Row.IntegracaoId,command.IntegrationId);Assert.Equal(Reason,command.Justification);
                return System.Net.Http.Json.JsonContent.Create(new AcmRetryAdmission("QUEUED","COMMITTED",command.CommandId,command.IntegrationId));
            }));
        var controller=new ProtocolRestRetryController(new(client),f,f);var preview=await controller.InspectAsync(f.Row,default);
        Assert.True((await controller.SubmitAsync(preview,Reason,true,default)).Accepted);
        Assert.Equal(1,methods.Count(m=>m=="POST"));Assert.Equal(3,methods.Count(m=>m=="GET"));
    }
    [Fact] public void Real_grid_click_uses_new_REST_handler_and_unsafe_record_sends_no_command()
    {
        GuardianProtocolFormTests.OnSta(()=>{
            var f=new Fixture();f.Row=f.Row with{ResultadoIndeterminado=true};f.Rows=[f.Row];
            using var form=new FrmProtocolosPreCadastro(new(f));form.ConfigureRestRetry(f.Controller);
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new(-20000,-20000);form.Show();Application.DoEvents();
            var grid=GuardianProtocolFormTests.Descendants(form).OfType<DataGridView>().Single();
            typeof(DataGridView).GetMethod("OnCellContentClick",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                .Invoke(grid,[new DataGridViewCellEventArgs(grid.Columns[FrmProtocolosPreCadastro.RestRetryColumn]!.Index,0)]);
            Application.DoEvents();Assert.Empty(f.Client.Requests);
            Assert.Contains(GuardianProtocolFormTests.Descendants(form).OfType<Label>(),l=>l.Text.Contains("indeterminado",StringComparison.OrdinalIgnoreCase));form.Close();
        });
    }
    sealed class Transport(Func<HttpRequestMessage,HttpContent> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>
            Task.FromResult(new HttpResponseMessage(request.Method==HttpMethod.Post?System.Net.HttpStatusCode.Accepted:System.Net.HttpStatusCode.OK){Content=respond(request)});
    }
    sealed class Fixture:IProtocolosPreCadastroReadRepository,IContratosAcmReadRepository
    {
        public ProtocoloPreCadastroLinha Row=new(){IntegracaoId=Guid.NewGuid(),Contrato="OFFLINE-ONLY",CorrelationId="CORR-OFFLINE",Referencia="OC-OFFLINE",EstadoMotor="AGUARDANDO_ACAO_MANUAL",ProcessamentoAtivo=true,ContratoConfirmado=true,Origem=ProtocoloPreCadastroLinha.Desconhecida};
        public ContratoAcmLeitura Contract;
        public IReadOnlyList<ProtocoloPreCadastroLinha> Rows;
        public IReadOnlyList<ContratoAcmLeitura> Contracts;
        public Client Client;
        public AcmManualRetryController Shared;
        public ProtocolRestRetryController Controller;
        public bool ThrowRead;
        public Fixture(){Contract=new("3011",Row.Contrato!,1,DateTime.UtcNow,"T",Row.EstadoMotor,Row.IntegracaoId,Row.CorrelationId,true){Item="10"};Rows=[Row];Contracts=[Contract];Client=new(Contract);Shared=new(Client);Controller=new(Shared,this,this);}
        public Task<IReadOnlyList<ProtocoloPreCadastroLinha>> ConsultarAsync(CancellationToken ct)=>ThrowRead?throw new Exception("private-token"):Task.FromResult(Rows);
        public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro,CancellationToken ct)=>Task.FromResult(Contracts);
    }
    sealed class Client(ContratoAcmLeitura row):IAcmManualRetryCommandClient
    {
        public bool Configured=>true;
        public string Digest=new('A',64);
        public AcmRetryDecision Decision=new(true,"REST_ONLY","Durable server proof",AcmRetryStage.RestOnly);
        public readonly List<AcmRetryRequest> Requests=[];
        public bool Unknown;public int Previews;public int SoapOnPreview=int.MaxValue;
        public TaskCompletionSource? Hold;
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AcmRetryPreview> PreviewAsync(ContratoAcmLeitura r,CancellationToken ct)=>Task.FromResult(new AcmRetryPreview(row.IntegracaoId,row.NumeroContrato,row.Item,row.EstadoIntegracao,"R165U_PROTOCOL_BLOCKED",Digest,++Previews>=SoapOnPreview?new(true,"SOAP","Wrong stage",AcmRetryStage.SoapThenRest):Decision,"S-1-5-21-1-2-3-1001"));
        public async Task<AcmRetryAdmission> SubmitAsync(AcmRetryRequest request,CancellationToken ct){Requests.Add(request);Entered.TrySetResult();if(Hold is not null)await Hold.Task;return new(Unknown?"UNKNOWN":"QUEUED","OFFLINE",request.CommandId,request.IntegrationId);}
    }
}
