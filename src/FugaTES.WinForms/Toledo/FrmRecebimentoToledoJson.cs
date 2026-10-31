using System.Text.Json;
using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
namespace FugaTES.WinForms.Toledo;
public sealed partial class FrmRecebimentoToledoJson:Form
{
 public Guid ReciboId{get;}
 public string JsonExibido=>json.Text;
 public FrmRecebimentoToledoJson(RecebimentoToledoLinha row,ToledoSapJsonVisualizacao doc)
 {
  if(row.ReciboId==Guid.Empty||row.ReciboId!=doc.ReciboId)throw new ArgumentException("Documento não corresponde ao recibo selecionado.");
  ReciboId=row.ReciboId;InitializeComponent();
  var label=doc.Estado switch {"ENVIADO_CONFERIDO"=>"Enviado — hash conferido","PREVIA_NAO_ENVIADA"=>"Prévia — não enviada","PREPARADO_SEM_POST_COMPROVADO"=>"Preparado — POST não comprovado",_=>"Indisponível"};
  Text="JSON SAP — "+label+" — Pesagem "+row.IdPesagem;
  json.Name="ToledoSapJson";json.AccessibleName="JSON SAP — "+label;
  if(doc.Json is not null)
  {using var parsed=JsonDocument.Parse(Guardian.FrmProtocoloJson.Sanitizar(doc.Json));json.Text=JsonSerializer.Serialize(parsed.RootElement,new JsonSerializerOptions{WriteIndented=true});}
  else {json.Text=doc.Mensagem;copiar.Enabled=false;}
  copiar.Name="ToledoCopySapJson";
  titulo.Text=$"{label} | Pesagem {row.IdPesagem} | Contrato(s): {row.Contratos} | Recibo {row.ReciboId}\r\nTentativa: {doc.TentativaId?.ToString()??"sem tentativa"} | Preparado UTC: {doc.PreparadoEm?.ToString("yyyy-MM-dd HH:mm:ss")??"—"} | HTTP: {doc.HttpStatus?.ToString()??"—"}";
  rodape.Text=doc.Mensagem+"\r\nSHA-256: "+(doc.Sha256??"não comprovado")+" | "+doc.Versao+" | "+doc.Projecao;
  copiar.Click+=(_,_)=>{try{Clipboard.SetText(json.Text);}catch(System.Runtime.InteropServices.ExternalException){rodape.Text="Não foi possível copiar: a área de transferência está ocupada.";}};
  fechar.Click+=(_,_)=>Close();
 }
 public FrmRecebimentoToledoJson(RecebimentoToledoLinha row,RecebimentoToledoDocumento doc)
 {
  if(row.ReciboId==Guid.Empty||row.ReciboId!=doc.ReciboId)throw new ArgumentException("Documento não corresponde ao recibo selecionado.");
  ReciboId=row.ReciboId;InitializeComponent();Text="JSON recebido — Toledo — Pesagem "+row.IdPesagem;
  using var parsed=JsonDocument.Parse(Guardian.FrmProtocoloJson.Sanitizar(doc.DocumentoJson));
  if(parsed.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Documento recebido inválido.");
  json.Text=JsonSerializer.Serialize(parsed.RootElement,new JsonSerializerOptions{WriteIndented=true});
  titulo.Text=$"Recibo {row.ReciboId} | Pesagem {row.IdPesagem} | Centro {row.Centro}\r\nContrato(s): {row.Contratos} | Recebido UTC: {row.RecebidoEm:yyyy-MM-dd HH:mm:ss}";
  rodape.Text="Conteúdo JSON persistido em PostgreSQL (JSONB), sem reconstrução empresarial. Formatação de apresentação; segredos redigidos se presentes.\r\nFingerprint armazenado: "+doc.FingerprintSha256;
  copiar.Click+=(_,_)=>{try{Clipboard.SetText(json.Text);}catch(System.Runtime.InteropServices.ExternalException){rodape.Text="Não foi possível copiar: a área de transferência está ocupada.";}};
  fechar.Click+=(_,_)=>Close();
 }
}
