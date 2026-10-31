using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
using FugaTES.Integracao.SAP.Http;
namespace FugaTES.WinForms.Toledo;
public sealed partial class FrmRecebimentosToledo
{
 public async Task<FrmRecebimentoToledoJson?> PrepararJsonSapAsync(int rowIndex)
 {
  if(busy||closing.IsCancellationRequested||rowIndex<0||rowIndex>=grid.Rows.Count||grid.Rows[rowIndex].DataBoundItem is not RecebimentoToledoLinha row)return null;
  if(repository is not IToledoSapJsonReadRepository reader)
  {status.ForeColor=Color.DarkRed;status.Text="A fonte de leitura não disponibiliza a evidência JSON SAP. Nenhum envio foi executado.";return null;}
  busy=true;consultar.Enabled=false;grid.Enabled=false;status.Text="Conferindo JSON SAP e evidência da tentativa no journal...";
  try
  {
   var source=await reader.ObterFonteJsonSapAsync(row.ReciboId,closing.Token);if(closing.IsCancellationRequested)return null;
   if(source.Original.ReciboId!=row.ReciboId)throw new InvalidOperationException("TOLEDO_READ_FOREIGN_RECEIPT");
   var view=ToledoSapDocumentoLeitura.Preparar(source);status.Text=view.Mensagem;status.ForeColor=view.Json is null?Color.DarkRed:Color.DarkGreen;
   return new FrmRecebimentoToledoJson(row,view);
  }
  catch(OperationCanceledException) when(closing.IsCancellationRequested){return null;}
  catch{status.ForeColor=Color.DarkRed;status.Text="JSON SAP indisponível. Verifique a API privada de leitura; nenhum documento foi substituído e nenhum envio foi executado.";return null;}
  finally{busy=false;if(!IsDisposed){consultar.Enabled=true;grid.Enabled=true;}}
 }
}
