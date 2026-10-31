using System.Windows.Forms;
using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.WinForms.Toledo;
namespace FugaTES.Testes;
public sealed class ToledoSapReturnUiTests
{
 static readonly Guid Receipt=Guid.Parse("99887766-1111-2222-3333-444444444444");
 static RecebimentoToledoLinha Row()=>new(Receipt,"TOLEDO","3011","900001",DateTime.Now,DateTimeOffset.UtcNow,"RECEIVED_PENDING_CORRELATION","OFFLINE","0099999901",1,"H4_NOT_FOUND");
 static PreviaReenvioToledoSap Preview()=>new(new(Receipt,Guid.NewGuid(),"FALHA_DEFINITIVA","FalhaAntesDoPost",503,1,false,false,true,false,true,new('A',64),new('B',64),DateTimeOffset.UtcNow),new(true,"Falha anterior ao POST"));
 [Fact] public void Screen_has_retry_and_original_JSON_but_no_manual_initial_send()
 {GuardianProtocolFormTests.OnSta(()=>{using var form=new FrmRecebimentosToledo(new Read());Finish(form.ConsultarAsync());var grid=GuardianProtocolFormTests.Descendants(form).OfType<DataGridView>().Single();Assert.True(grid.Columns.Contains("json"));Assert.True(grid.Columns.Contains("reenviarSap"));Assert.True(grid.Columns.Contains("EstadoSap"));Assert.True(grid.Columns.Contains("HttpSap"));Assert.Equal(1,grid.Columns["jsonSap"]!.DisplayIndex);Assert.Equal(2,grid.Columns["reenviarSap"]!.DisplayIndex);Assert.Equal(5,grid.Columns["EstadoSap"]!.DisplayIndex);Assert.DoesNotContain(GuardianProtocolFormTests.Descendants(form),c=>c is Button&&c.Text=="Enviar");Assert.Contains("Não iniciado",Row().EstadoSap);});}
 [Fact] public void Missing_command_does_not_turn_a_received_receipt_into_a_sent_weighing()
 {GuardianProtocolFormTests.OnSta(()=>{using var form=new FrmRecebimentosToledo(new Read(Guid.NewGuid()));Finish(form.ConsultarAsync());Assert.Null(Finish(form.PrepararReenvioSapAsync(0)));Assert.Contains("Comando de recuperação SAP indisponível",Status(form));});}
 [Fact] public void Retry_is_confirmed_justified_and_routes_only_to_the_command_client_once()
 {GuardianProtocolFormTests.OnSta(()=>{var command=new Commands();using var form=new FrmRecebimentosToledo(new Read(),sapRetry:command);Finish(form.ConsultarAsync());var preview=Finish(form.PrepararReenvioSapAsync(0));Assert.NotNull(preview);Assert.Null(Finish(form.SolicitarReenvioSapAsync(preview,"Justificativa offline comprovada",false)));Assert.Equal(0,command.Writes);var result=Finish(form.SolicitarReenvioSapAsync(preview,"Justificativa offline comprovada",true));Assert.Equal("QUEUED",result!.Estado);Assert.Null(Finish(form.SolicitarReenvioSapAsync(preview,"Justificativa offline comprovada",true)));Assert.Equal(1,command.Writes);Assert.Equal(preview.Snapshot.IntegracaoId,command.Request!.IntegracaoId);});}
 [Fact] public void New_persisted_failure_and_new_authorized_revision_release_only_the_old_revision_latch()
 {GuardianProtocolFormTests.OnSta(()=>{var id=Guid.NewGuid();var command=new Commands();command.Preview=command.Preview with{Snapshot=command.Preview.Snapshot with{ReciboId=id}};using var form=new FrmRecebimentosToledo(new Read(id),sapRetry:command);Finish(form.ConsultarAsync());var first=Finish(form.PrepararReenvioSapAsync(0));Assert.NotNull(first);Assert.Equal("QUEUED",Finish(form.SolicitarReenvioSapAsync(first,"Justificativa offline comprovada",true))!.Estado);Assert.Null(Finish(form.PrepararReenvioSapAsync(0)));command.Preview=command.Preview with{Snapshot=command.Preview.Snapshot with{Tentativas=2,Revisao=new('C',64)}};Assert.NotNull(Finish(form.PrepararReenvioSapAsync(0)));});}
 [Fact] public void Foreign_unknown_or_completed_previews_are_not_dispatchable()
 {GuardianProtocolFormTests.OnSta(()=>{foreach(var state in new[]{"CONCLUIDA","RESULTADO_INDETERMINADO","EM_PROCESSAMENTO"}){var command=new Commands();command.Preview=command.Preview with{Snapshot=command.Preview.Snapshot with{Estado=state}};using var form=new FrmRecebimentosToledo(new Read(),sapRetry:command);Finish(form.ConsultarAsync());Assert.Null(Finish(form.PrepararReenvioSapAsync(0)));Assert.Equal(0,command.Writes);}});}
 [Fact] public void HTTP200_is_explicitly_not_invented_business_success()
 {var row=Row() with{DevolutivaSap=Preview().Snapshot with{Codigo="AceiteHttpSemConfirmacaoEmpresarial",HttpStatus=200,Estado="RESULTADO_INDETERMINADO"}};Assert.Contains("não comprovada",row.ResultadoSap);Assert.Equal("200",row.HttpSap);Assert.Contains("reconciliação",row.EstadoSap);}
 [Theory][InlineData("RecebimentoConfirmadoSap","201")][InlineData("RecebimentoJaConfirmadoSap","200")]
 public void Confirmed_receipts_show_success_but_remain_non_retryable(string code,string http)
 {var row=Row() with{DevolutivaSap=Preview().Snapshot with{Codigo=code,HttpStatus=int.Parse(http),Estado="CONCLUIDA"}};Assert.Contains("confirm",row.ResultadoSap,StringComparison.OrdinalIgnoreCase);Assert.Equal(http,row.HttpSap);Assert.False(DevolutivaToledoSapPolicy.Avaliar(row.DevolutivaSap,true).Permitido);}
 static string Status(Form f)=>GuardianProtocolFormTests.Descendants(f).Single(c=>c.Name=="ToledoReceiptStatus").Text;
 static void Finish(Task task){while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(1);}task.GetAwaiter().GetResult();}
 static T Finish<T>(Task<T> task){Finish((Task)task);return task.GetAwaiter().GetResult();}
 sealed class Read(Guid? id=null):IRecebimentosToledoReadRepository
 {public Task<RecebimentosToledoSnapshot> ConsultarRecebimentosAsync(string? s,CancellationToken ct)=>Task.FromResult(new RecebimentosToledoSnapshot([Row() with{ReciboId=id??Receipt}],false,DateTimeOffset.UtcNow));public Task<RecebimentoToledoDocumento> ObterJsonRecebidoAsync(Guid id,CancellationToken ct)=>Task.FromResult(new RecebimentoToledoDocumento(id,"{}",new('A',64)));}
 sealed class Commands:IComandoReenvioToledoSapClient
 {public bool Configurado=>true;public int Writes;public PreviaReenvioToledoSap Preview=ToledoSapReturnUiTests.Preview();public ComandoReenvioToledoSap? Request;
 public Task<PreviaReenvioToledoSap> PrepararAsync(Guid id,CancellationToken ct)=>Task.FromResult(Preview);
 public Task<ReciboComandoToledoSap> SolicitarAsync(ComandoReenvioToledoSap c,CancellationToken ct){Writes++;Request=c;return Task.FromResult(new ReciboComandoToledoSap(c.ComandoId,c.ReciboId,"QUEUED"));}}
}
