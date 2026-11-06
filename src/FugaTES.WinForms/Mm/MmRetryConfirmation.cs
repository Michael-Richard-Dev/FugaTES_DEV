using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.WinForms.Mm;
public sealed partial class MmRetryConfirmation:Form
{
 public string Justification=>justification.Text.Trim();
 public MmRetryConfirmation(MmRetryPreview preview)
 {
  InitializeComponent();facts.Text=$"Pedido {preview.Pedido} · item {preview.Item}\r\nEtapa: criar protocolo Guardian (REST), sem repetir SAP/recepção.\r\n{preview.Decision.Message}\r\nOperador autenticado: {preview.OperatorSid}";
  justification.TextChanged+=(_,_)=>accept.Enabled=Justification.Length is >=8 and <=500&&!Justification.Any(char.IsControl);
 }
}
