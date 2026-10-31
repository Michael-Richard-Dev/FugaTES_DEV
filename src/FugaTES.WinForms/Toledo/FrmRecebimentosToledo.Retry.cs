using System.Collections.Concurrent;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Aplicacao.Servicos.ManualRetry;
namespace FugaTES.WinForms.Toledo;

public sealed partial class FrmRecebimentosToledo
{
 private readonly IComandoReenvioToledoSapClient? sapRetry;
 private static readonly ConcurrentDictionary<Guid,byte> pendingCommands=new();
 private static readonly ConcurrentDictionary<Guid,string> latchedCommands=new();
 public async Task<PreviaReenvioToledoSap?> PrepararReenvioSapAsync(int rowIndex)
 {
  if(busy||closing.IsCancellationRequested||rowIndex<0||rowIndex>=rows.Count)return null;
  var row=rows[rowIndex];
  if(sapRetry?.Configurado!=true){status.Text=row.MotivoReenvio+" Comando de recuperação SAP indisponível nesta instalação.";return null;}
  busy=true;grid.Enabled=false;consultar.Enabled=false;
  try
  {
   var preview=await sapRetry.PrepararAsync(row.ReciboId,closing.Token);
   if(preview.Snapshot.ReciboId!=row.ReciboId||!preview.Decisao.Permitido||!DevolutivaToledoSapPolicy.Avaliar(preview.Snapshot,true).Permitido)
   {status.Text=preview.Snapshot.ReciboId==row.ReciboId?(!preview.Decisao.Permitido?preview.Decisao.Motivo:DevolutivaToledoSapPolicy.Avaliar(preview.Snapshot,true).Motivo):"Identidade do recibo não confere; nenhum comando enviado.";return null;}
   if(latchedCommands.TryGetValue(row.ReciboId,out var revision))
   {
    if(revision==preview.Snapshot.Revisao){status.Text="Comando já solicitado ou resultado incerto; conferir o journal antes de repetir.";return null;}
    latchedCommands.TryRemove(row.ReciboId,out _); // Only a fresh server-authorized eligible terminal snapshot may clear it.
   }
   return preview;
  }
  catch{status.Text="Preflight de reenvio indisponível; nenhum comando foi enviado.";return null;}
  finally{busy=false;if(!IsDisposed){grid.Enabled=true;consultar.Enabled=true;}}
 }
 public async Task<ReciboComandoToledoSap?> SolicitarReenvioSapAsync(PreviaReenvioToledoSap preview,string justification,bool confirmed)
 {
  var s=preview.Snapshot;
  if(closing.IsCancellationRequested||!confirmed||sapRetry?.Configurado!=true||!preview.Decisao.Permitido||
    !DevolutivaToledoSapPolicy.Avaliar(s,true).Permitido||!AcmManualRetryService.SafeJustification(justification)||
    !rows.Any(r=>r.ReciboId==s.ReciboId)||latchedCommands.ContainsKey(s.ReciboId)||!pendingCommands.TryAdd(s.ReciboId,0))return null;
  var id=Guid.NewGuid();busy=true;grid.Enabled=false;consultar.Enabled=false;
  try
  {
   var result=await sapRetry.SolicitarAsync(new(id,s.ReciboId,s.IntegracaoId,s.Revisao,justification,true),closing.Token);
   if(result.ComandoId!=id||result.ReciboId!=s.ReciboId||result.Estado is not("QUEUED" or "BLOCKED"))throw new InvalidOperationException();
   if(result.Estado=="QUEUED")latchedCommands.TryAdd(s.ReciboId,s.Revisao);
   status.Text=result.Estado=="QUEUED"?"Comando persistido na fila. Acompanhe o resultado SAP; não repita o clique.":"Comando recusado pelo servidor; nenhuma nova tentativa autorizada.";
   return result;
  }
  catch{latchedCommands.TryAdd(s.ReciboId,s.Revisao);status.Text="Resultado do comando não confirmado. Consulte o journal; não repetir automaticamente.";return null;}
  finally{pendingCommands.TryRemove(s.ReciboId,out _);busy=false;if(!IsDisposed){grid.Enabled=true;consultar.Enabled=true;}}
 }
 private async Task ReenviarLinhaAsync(int index)
 {
  var preview=await PrepararReenvioSapAsync(index);if(preview is null||IsDisposed)return;
  using var confirm=new ManualRetry.AcmRetryConfirmation(preview,rows[index].Contratos);
  if(confirm.ShowDialog(this)!=DialogResult.OK)return;
  var result=await SolicitarReenvioSapAsync(preview,confirm.Justification,true);
  if(result?.Estado=="QUEUED")await ConsultarAsync();
 }
}
