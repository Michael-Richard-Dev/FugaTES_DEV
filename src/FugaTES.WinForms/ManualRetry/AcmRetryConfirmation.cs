using FugaTES.Aplicacao.Servicos.ManualRetry;

namespace FugaTES.WinForms.ManualRetry;

internal sealed partial class AcmRetryConfirmation:Form
{
    public string Justification=>reason.Text.Trim();
    static string StageLabel(AcmRetryStage stage)=>stage switch
    {
        AcmRetryStage.SoapOnly=>"Somente cadastro SOAP; protocolo pendente (REST não autorizado)",
        AcmRetryStage.SoapThenRest=>"SOAP e REST conforme autorização nominal",
        AcmRetryStage.RestOnly=>"Somente REST; SOAP não será repetido",
        _=>"Bloqueada: etapa não autorizada"
    };
    public AcmRetryConfirmation(FugaTES.Aplicacao.Servicos.Recebimento.PreviaReenvioToledoSap preview,string contract)
    {
        InitializeComponent();
        summary.Text=$"Devolutiva SAP — contrato(s): {contract}\r\nRecibo: {preview.Snapshot.ReciboId}\r\nIntegrationId: {preview.Snapshot.IntegracaoId}\r\nEstado: {preview.Snapshot.Estado} | Etapa: POST ReceberPesagem\r\nMotivo: {preview.Decisao.Motivo}\r\nO documento original persistido será preservado. Nenhum SOAP Guardian será repetido.";
        reason.TextChanged+=(_,_)=>confirm.Enabled=AcmManualRetryService.SafeJustification(Justification);
    }
    public AcmRetryConfirmation(AcmRetryPreview preview,string? protocolReference=null)
    {
        InitializeComponent();
        summary.Text=$"Contrato: {preview.Contract} | Item: {preview.Item??"ausente"}\r\nIntegrationId: {preview.IntegrationId}\r\nEstado: {preview.State} | Etapa: {StageLabel(preview.Decision.Stage)}\r\nOperador autenticado: {preview.OperatorSid}\r\nMotivo: {preview.Decision.Message}\r\nO payload SAP original será preservado. Resultado indeterminado bloqueia nova tentativa.";
        if(protocolReference is not null)summary.Text+="\r\nReferência: "+protocolReference+"\r\nRecuperação exclusiva REST ProtocoloPreCadastro. SOAP e recebimento SAP não serão repetidos.";
        reason.TextChanged+=(_,_)=>confirm.Enabled=AcmManualRetryService.SafeJustification(Justification);
    }
}
