using FugaTES.WinForms;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.WinForms.ManualRetry;

public sealed class ManualRetryUiTests
{
    [Fact]
    public void Official_contract_grid_has_retry_and_preserves_JSON()
    {
        Exception? failure=null;
        var t=new Thread(()=>{try{
            using var form=new FrmContratosAcm(new(new Empty()));
            var grid=All(form).OfType<DataGridView>().Single();
            Assert.NotNull(grid.Columns["json"]);
            Assert.NotNull(grid.Columns["reenviar"]);
        }catch(Exception e){failure=e;}});
        t.SetApartmentState(ApartmentState.STA);t.Start();t.Join();Assert.Null(failure);
    }
    [Fact] public void Nominal_confirmation_explicitly_shows_SOAP_only_and_pending_protocol()
    {
        FugaTES.Testes.GuardianProtocolFormTests.OnSta(()=>{
            var type=typeof(FrmContratosAcm).Assembly.GetType("FugaTES.WinForms.ManualRetry.AcmRetryConfirmation",true)!;
            var preview=new AcmRetryPreview(Guid.NewGuid(),"0099999975",null,"PENDENTE",null,new string('A',64),new(true,"PENDING_LATEST_SAFE_ADMISSION","Somente SOAP",(AcmRetryStage)3),"S-1-5-21-1-2-3-1001");
            using var form=(Form)Activator.CreateInstance(type,[preview,null])!;
            var text=string.Join(" ",All(form).Select(x=>x.Text));Assert.Contains("somente cadastro SOAP",text,StringComparison.OrdinalIgnoreCase);Assert.Contains("protocolo pendente",text,StringComparison.OrdinalIgnoreCase);
        });
    }

    static ContratoAcmLeitura Row()=>new("3011","OFFLINE-ONLY",1,DateTime.UtcNow,"T","FALHA_DEFINITIVA",Guid.NewGuid(),"OFFLINE",true){Item="10"};
    [Fact]public async Task Double_click_and_subsequent_click_never_create_two_commands()
    {
        var row=Row();var client=new PendingClient(row);var controller=new AcmManualRetryController(client);var preview=await controller.PreviewAsync(row,default);
        var first=controller.SubmitAsync(row,preview,"Causa corrigida comprovada para teste",true,default);await client.Entered.Task;
        var second=await controller.SubmitAsync(row,preview,"Causa corrigida comprovada para teste",true,default);
        Assert.Equal("BLOCKED",second.State);Assert.Equal(1,client.Commands);
        client.Commit.TrySetResult();Assert.True((await first).Accepted);
        Assert.Equal("BLOCKED",(await controller.SubmitAsync(row,preview,"Causa corrigida comprovada para teste",true,default)).State);Assert.Equal(1,client.Commands);
    }
    [Fact]public async Task Unknown_command_is_latched_and_not_automatically_retried()
    {
        var row=Row();var client=new PendingClient(row){Fail=true};client.Commit.TrySetResult();var controller=new AcmManualRetryController(client);var preview=await controller.PreviewAsync(row,default);
        Assert.Equal("UNKNOWN",(await controller.SubmitAsync(row,preview,"Causa corrigida comprovada para teste",true,default)).State);
        Assert.Equal("BLOCKED",(await controller.SubmitAsync(row,preview,"Causa corrigida comprovada para teste",true,default)).State);Assert.Equal(1,client.Commands);
    }
    [Fact]public async Task Installed_readonly_binding_cannot_send_even_a_forged_eligible_preview()
    {
        var row=Row() with{FonteDados="SIMULACAO_CONFIRMADA"};var client=new ReadOnlyAcmManualRetryClient();var controller=new AcmManualRetryController(client);
        var preview=await controller.PreviewAsync(row,default);Assert.False(preview.Decision.Eligible);Assert.Equal("SIMULATION",preview.Decision.Code);
        Assert.False((await controller.SubmitAsync(row,preview with{Decision=new(true,"FORGED","Not a real authorization")},"Causa corrigida comprovada para teste",true,default)).Accepted);
    }
    [Fact]public async Task Initial_readonly_preview_does_not_fetch_a_detail_for_every_grid_row()
    {
        var calls=0;var client=new ReadOnlyAcmManualRetryClient((id,ct)=>{calls++;throw new InvalidOperationException("DETAIL_ONLY_ON_DEMAND");});
        await client.PreviewAsync(Row(),default);Assert.Equal(0,calls);
    }
    [Fact]public void Evidence_probe_of_configured_client_never_submits_even_when_preview_is_eligible()
    {
        FugaTES.Testes.GuardianProtocolFormTests.OnSta(()=>
        {
            var row=Row();var client=new PendingClient(row);
            using var form=new FrmContratosAcm(new(new OneRow(row)),manualRetry:client);
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new(-20000,-20000);form.Show();Application.DoEvents();
            var probe=typeof(FrmContratosAcm).GetMethod("InspectRetryForEvidenceAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            ((Task)probe.Invoke(form,[row.IntegracaoId])!).GetAwaiter().GetResult();
            Assert.Equal(0,client.Commands);form.Close();
        });
    }
    [Fact]public async Task Configured_service_keeps_the_concrete_indeterminate_block_instead_of_asking_permission_again()
    {
        var row=Row() with{EstadoIntegracao="RESULTADO_INDETERMINADO"};var controller=new AcmManualRetryController(new ScopeAbsentClient());
        var preview=await controller.InspectAsync(row,default);
        Assert.False(preview.Decision.Eligible);Assert.Equal("RECONCILIATION_REQUIRED",preview.Decision.Code);
        Assert.Contains("indeterminado",preview.Decision.Message,StringComparison.OrdinalIgnoreCase);
    }
    sealed class ScopeAbsentClient:IAcmManualRetryClient
    {
        public bool Configured=>true;
        public Task<AcmRetryPreview> PreviewAsync(ContratoAcmLeitura row,CancellationToken ct)=>Task.FromResult(new AcmRetryPreview(row.IntegracaoId,"",null,"BLOCKED",null,"",new(false,"COMMAND_NOT_AUTHORIZED","Sem escopo"),"S-1-5-21-1-2-3-1001"));
        public Task<AcmRetryAdmission> SubmitAsync(AcmRetryRequest request,CancellationToken ct)=>throw new InvalidOperationException("NEVER_SUBMIT");
    }
    sealed class OneRow(ContratoAcmLeitura row):IContratosAcmReadRepository
    {public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ContratoAcmLeitura>>([row]);}
    sealed class PendingClient(ContratoAcmLeitura row):IAcmManualRetryClient
    {
        public bool Configured=>true;public int Commands;public bool Fail;
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Commit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<AcmRetryPreview> PreviewAsync(ContratoAcmLeitura r,CancellationToken ct)=>Task.FromResult(new AcmRetryPreview(row.IntegracaoId,row.NumeroContrato,row.Item,row.EstadoIntegracao,"R165U_PROTOCOL_BLOCKED",new string('A',64),new(true,"REST_ONLY","OFFLINE ONLY",AcmRetryStage.RestOnly),"S-1-5-21-1-2-3-1001"));
        public async Task<AcmRetryAdmission> SubmitAsync(AcmRetryRequest request,CancellationToken ct)
        {Commands++;Entered.TrySetResult();await Commit.Task;if(Fail)throw new InvalidOperationException("UNKNOWN");return new("QUEUED","OFFLINE",request.CommandId,request.IntegrationId);}
    }
    static IEnumerable<Control> All(Control c)=>c.Controls.Cast<Control>().SelectMany(x=>new[]{x}.Concat(All(x)));
    sealed class Empty:IContratosAcmReadRepository
    {public Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ContratoAcmLeitura>>([]);}
}
