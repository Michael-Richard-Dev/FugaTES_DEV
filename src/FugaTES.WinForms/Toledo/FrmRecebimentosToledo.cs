using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
namespace FugaTES.WinForms.Toledo;

public sealed partial class FrmRecebimentosToledo:Form
{
 private readonly IRecebimentosToledoReadRepository repository;
 private readonly CancellationTokenSource closing=new();
 private readonly Func<string>? sourceStatus;
 private IReadOnlyList<RecebimentoToledoLinha> rows=[];
 private bool busy,resourcesReleased;
 public IReadOnlyList<RecebimentoToledoLinha> Recebimentos=>rows;
 public FrmRecebimentosToledo(IRecebimentosToledoReadRepository repository,Func<string>? sourceStatus=null,FugaTES.Aplicacao.Servicos.Recebimento.IComandoReenvioToledoSapClient? sapRetry=null)
 {
  this.repository=repository??throw new ArgumentNullException(nameof(repository));this.sourceStatus=sourceStatus;this.sapRetry=sapRetry;
  InitializeComponent();consultar.Click+=async(_,_)=>await ConsultarAsync();Shown+=async(_,_)=>await ConsultarAsync();
  busca.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await ConsultarAsync();}};
  grid.CellContentClick+=async(_,e)=>
  {
   if(e.ColumnIndex<0)return;
   if(grid.Columns[e.ColumnIndex].Name=="json")
   {using var form=await PrepararJsonAsync(e.RowIndex);if(form is not null&&!IsDisposed)form.ShowDialog(this);}
   else if(grid.Columns[e.ColumnIndex].Name=="jsonSap")
   {using var form=await PrepararJsonSapAsync(e.RowIndex);if(form is not null&&!IsDisposed)form.ShowDialog(this);}
   else if(grid.Columns[e.ColumnIndex].Name=="reenviarSap")await ReenviarLinhaAsync(e.RowIndex);
  };
 }
 public async Task ConsultarAsync()
 {
  if(busy||closing.IsCancellationRequested)return;busy=true;consultar.Enabled=false;grid.Enabled=false;
  status.Text="Consultando recebimentos persistidos no PostgreSQL DEV5433...";
  try
  {
   var filtro=RecebimentosToledoFiltro.Normalizar(busca.Text);
   var result=await repository.ConsultarRecebimentosAsync(filtro,closing.Token);
   if(closing.IsCancellationRequested)return;
   rows=result.Rows;grid.DataSource=rows.ToArray();status.ForeColor=Color.DarkGreen;
   status.Text=$"{rows.Count} recebimento(s). Atualizado em {result.CapturadoEm.ToLocalTime():dd/MM/yyyy HH:mm:ss}. "+
    (result.HasMore?"Existem mais registros: refine o filtro.":"Acompanhamento do recebimento e da devolutiva SAP; Reenviar exige falha comprovada e comando autorizado.");
   fonte.Text="POSTGRESQL DEV REAL · fugates_dev · porta física5433 · "+(sourceStatus?.Invoke()??"API privada de leitura");
  }
  catch(OperationCanceledException) when(closing.IsCancellationRequested){}
  catch(ArgumentException ex){status.ForeColor=Color.DarkRed;status.Text=ex.Message;}
  catch{rows=[];grid.DataSource=rows.ToArray();status.ForeColor=Color.DarkRed;status.Text="Leitura indisponível. Verifique conexão e permissões da API privada; nenhum dado foi substituído por demonstração.";}
  finally{busy=false;if(!IsDisposed){consultar.Enabled=true;grid.Enabled=true;}}
 }
 public async Task<FrmRecebimentoToledoJson?> PrepararJsonAsync(int rowIndex)
 {
  if(busy||closing.IsCancellationRequested||rowIndex<0||rowIndex>=grid.Rows.Count||grid.Rows[rowIndex].DataBoundItem is not RecebimentoToledoLinha row)return null;
  busy=true;consultar.Enabled=false;grid.Enabled=false;status.Text="Lendo JSON persistido do recebimento selecionado...";
  try
  {
   var doc=await repository.ObterJsonRecebidoAsync(row.ReciboId,closing.Token);
   if(closing.IsCancellationRequested)return null;
   if(doc.ReciboId!=row.ReciboId)throw new InvalidOperationException("TOLEDO_READ_FOREIGN_RECEIPT");
   var form=new FrmRecebimentoToledoJson(row,doc);status.ForeColor=Color.DarkGreen;
   status.Text="JSON recebido carregado da persistência. Nenhum reenvio ou alteração foi executado.";return form;
  }
  catch(OperationCanceledException) when(closing.IsCancellationRequested){return null;}
  catch{status.ForeColor=Color.DarkRed;status.Text="Não foi possível ler o JSON deste recibo. Nenhum documento foi reconstruído ou substituído.";return null;}
  finally{busy=false;if(!IsDisposed){consultar.Enabled=true;grid.Enabled=true;}}
 }
 private void ReleaseResources(){if(resourcesReleased)return;resourcesReleased=true;closing.Cancel();closing.Dispose();}
}
