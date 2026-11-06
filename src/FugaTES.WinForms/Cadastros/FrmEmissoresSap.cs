using FugaTES.Aplicacao.Consultas.EmissoresSap;
namespace FugaTES.WinForms.Cadastros;
public partial class FrmEmissoresSap:Form
{
 readonly IEmissoresSapConsulta? consulta;
 readonly IDisposable? recursos;
 readonly CancellationTokenSource vida=new();
 readonly List<EmissoresSapPagina> paginas=[];
 EmissoresSapFiltro filtro=new();
 EmissorSapDetalhe? detalhe;
 int indice=-1;
 bool ocupado,liberado;
 public FrmEmissoresSap(IEmissoresSapConsulta? consulta=null,IDisposable? recursos=null,string? bloqueio=null)
 {
  this.consulta=consulta;this.recursos=recursos;InitializeComponent();
  consultar.Enabled=consulta is not null;
  if(consulta is null)status.Text="DESCONECTADO — configuração de leitura SAP indisponível. "+(bloqueio??"SAP_READ_NOT_CONFIGURED");
  consultar.Click+=async(_,_)=>await AtualizarAsync();
  anterior.Click+=(_,_)=>{if(!ocupado&&indice>0){indice--;MostrarPagina();}};
  proxima.Click+=async(_,_)=>
  {
   if(ocupado||indice<0)return;
   if(indice+1<paginas.Count){indice++;MostrarPagina();}
   else if(paginas[indice].Continuacao is {} cursor)await LerPaginaAsync(cursor);
  };
  grid.CellContentClick+=async(_,e)=>
  {
   if(ocupado||e.RowIndex<0||e.ColumnIndex<0||grid.Rows[e.RowIndex].DataBoundItem is not EmissorSapLeitura row)return;
   var column=grid.Columns[e.ColumnIndex].Name;
   if(column is "Detalhes" or "Json")await CarregarDetalheAsync(row.Codigo,column=="Json");
  };
  FormClosing+=(_,_)=>vida.Cancel();
 }
 public async Task AtualizarAsync()
 {
  if(ocupado||consulta is null||liberado)return;
  filtro=new(codigo.Text.Trim(),nome.Text.Trim());paginas.Clear();indice=-1;grid.DataSource=null;LimparDetalhe();
  await LerPaginaAsync(null);
 }
 async Task LerPaginaAsync(string? cursor)
 {
  if(consulta is null||liberado)return;
  Ocupar(true);status.Text="CONSULTANDO SAP — leitura de emissores; nenhum envio Guardian.";
  try
  {
   var page=await consulta.ConsultarAsync(filtro,cursor,vida.Token);vida.Token.ThrowIfCancellationRequested();
   paginas.Add(page);indice=paginas.Count-1;MostrarPagina();
  }
  catch(OperationCanceledException)when(vida.IsCancellationRequested){}
  catch(Exception ex)
  {if(!liberado&&!vida.IsCancellationRequested){paginas.Clear();indice=-1;grid.DataSource=null;LimparDetalhe();pagina.Text="";Falha(ex);}}
  finally{if(!liberado&&!vida.IsCancellationRequested)Ocupar(false);}
 }
 void MostrarPagina()
 {
  LimparDetalhe();var p=paginas[indice];grid.DataSource=p.Registros.ToArray();
  status.Text="CONECTADO — SAP HTTPS · mandante 110 · somente GET.\r\nÚltima leitura: "+p.LidoEm.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz")+". Guardian não consultado.";
  pagina.Text=$"Página {indice+1} · {p.Registros.Count} registro(s)"+(p.Total is {} n?$" · total SAP: {n}":" · total não informado pelo SAP");
  rodape.Text=p.Registros.Count==0?"Nenhum emissor encontrado para os filtros informados. A consulta SAP foi concluída.":"Dados consultados no SAP, sem cadastro mestre local. Abra Detalhes SAP ou JSON SAP; não há envio Guardian nesta etapa.";
  Navegacao();
 }
 public async Task CarregarDetalheAsync(string bp,bool abrirJson=false)
 {
  if(ocupado||consulta is null||liberado||indice<0||!paginas[indice].Registros.Any(x=>x.Codigo==bp))return;
  Ocupar(true);detalheStatus.Text="Consultando os dados completos no SAP...";
  try
  {
   var value=detalhe?.Codigo==bp?detalhe:await consulta.ObterDetalheAsync(bp,vida.Token);vida.Token.ThrowIfCancellationRequested();
   if(value.Codigo!=bp)throw new EmissoresSapException("SAP_IDENTITY_MISMATCH");
   detalhe=value;dados.DataSource=value.Dados.ToArray();json.Text=value.Json;detalhes.SelectedIndex=abrirJson?1:0;
   detalheStatus.Text=$"BP {bp} · leitura detalhada: {value.LidoEm.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz} · campos ausentes não são preenchidos artificialmente.";
  }
  catch(OperationCanceledException)when(vida.IsCancellationRequested){}
  catch(Exception ex){if(!liberado&&!vida.IsCancellationRequested){LimparDetalhe();Falha(ex);}}
  finally{if(!liberado&&!vida.IsCancellationRequested)Ocupar(false);}
 }
 void LimparDetalhe(){detalhe=null;dados.DataSource=null;json.Clear();detalheStatus.Text="Selecione Detalhes SAP ou JSON SAP para consultar todos os dados disponíveis desse BP.";}
 void Falha(Exception ex)
 {
  var code=ex is EmissoresSapException e?e.Codigo:"SAP_READ_FAILED";
  var motivo=code switch{"SAP_UNAUTHORIZED"=>"SAP recusou a autenticação.","SAP_FORBIDDEN"=>"Usuário sem autorização para esta leitura SAP.","SAP_FILTER_INVALID"=>"Informe código BP de até 10 posições e nome sem caracteres de controle.","SAP_TIMEOUT"=>"Tempo limite da consulta SAP excedido.","SAP_UNAVAILABLE"=>"Não foi possível alcançar o SAP.","SAP_DETAIL_INCOMPLETE"=>"SAP retornou detalhe incompleto; nenhum dado foi presumido.",_=>"A leitura não pôde ser validada. Atualize a consulta ou confira a configuração SAP."};
  status.Text=$"DESCONECTADO / leitura não confirmada — {motivo} [{code}]";rodape.Text="Falha de leitura não significa ausência do cadastro. Nenhuma operação Guardian foi executada.";
 }
 void Ocupar(bool value){ocupado=value;consultar.Enabled=!value&&consulta is not null;codigo.Enabled=!value;nome.Enabled=!value;grid.Enabled=!value;Navegacao();UseWaitCursor=value;}
 void Navegacao(){anterior.Enabled=!ocupado&&indice>0;proxima.Enabled=!ocupado&&indice>=0&&(indice+1<paginas.Count||paginas[indice].Continuacao is not null);}
 void Liberar(){if(liberado)return;liberado=true;vida.Cancel();recursos?.Dispose();vida.Dispose();}
}
