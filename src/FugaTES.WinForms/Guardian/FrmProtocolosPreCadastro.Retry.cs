using FugaTES.Aplicacao.Consultas.ProtocolosGuardian;
using FugaTES.WinForms.ManualRetry;

namespace FugaTES.WinForms.Guardian;

public sealed partial class FrmProtocolosPreCadastro
{
    ProtocolRestRetryController? restRetry;
    Action? retryChanged;
    bool commandBusy;
    public const string RestRetryColumn="restRetryCommand";

    // Keep the nine original Claude files immutable. Rename only the runtime column key so
    // the legacy information-only handler cannot intercept the new command button. JSON is unchanged.
    public void ConfigureRestRetry(ProtocolRestRetryController controller,Action? changed=null)
    {
        if(restRetry is not null)throw new InvalidOperationException("PROTOCOL_RETRY_ALREADY_BOUND");
        restRetry=controller??throw new ArgumentNullException(nameof(controller));retryChanged=changed;
        reenviar.Name=RestRetryColumn;
        reenviar.ToolTipText="Conferir e solicitar recuperação exclusivamente REST ao serviço autenticado; SOAP não será repetido.";
        grid.CellContentClick+=async(_,e)=>
        {
            if(e.RowIndex>=0&&e.ColumnIndex>=0&&grid.Columns[e.ColumnIndex].Name==RestRetryColumn&&
                grid.Rows[e.RowIndex].DataBoundItem is ProtocoloPreCadastroExibicao row)
                await RequestRestRetryAsync(row.Origemlinha);
        };
        grid.CellFormatting+=(_,e)=>
        {
            if(e.RowIndex<0||grid.Rows[e.RowIndex].DataBoundItem is not ProtocoloPreCadastroExibicao row)return;
            var reason=ProtocolRestRetryController.ProjectionBlockReason(row.Origemlinha);
            if(grid.Columns[e.ColumnIndex].Name==RestRetryColumn)
            {
                e.CellStyle!.ForeColor=!commandBusy&&controller.Configured&&reason is null?SystemColors.ControlText:SystemColors.GrayText;
                grid.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText=reason??"Consultar aprovação nominal, causa corrigida e etapa REST no servidor.";
            }
            if(grid.Columns[e.ColumnIndex].Name=="Reenvio")
            {e.Value=reason??(controller.Configured?"PREFLIGHT REST NO SERVIDOR":"COMANDO NÃO CONFIGURADO");e.FormattingApplied=true;}
        };
    }
    internal object ExportRestRetryReadiness()=>new{commandConfigured=restRetry?.Configured==true,
        stage="REST_ONLY",soapAllowed=false,button=reenviar.Name,message=status.Text};
    internal async Task InspectRestRetryForEvidenceAsync(Guid id)
    {
        // This observer has no path to confirmation or Submit, even for an eligible record.
        var row=linhas.Single(r=>r.Origemlinha.IntegracaoId==id).Origemlinha;
        var preview=await restRetry!.InspectAsync(row,cancelamento.Token);
        status.Text="Prévia REST somente leitura — "+preview.Command.Decision.Message;
    }
    async Task RequestRestRetryAsync(ProtocoloPreCadastroLinha row)
    {
        if(commandBusy||restRetry is null)return;
        commandBusy=true;atualizar.Enabled=false;grid.Invalidate();
        try
        {
            var preview=await restRetry.InspectAsync(row,cancelamento.Token);
            if(!preview.Command.Decision.Eligible){status.Text="Reenvio REST bloqueado — "+preview.Command.Decision.Message;return;}
            using var confirmation=new AcmRetryConfirmation(preview.Command,preview.Protocol.Referencia);
            if(confirmation.ShowDialog(this)!=DialogResult.OK)return;
            var receipt=await restRetry.SubmitAsync(preview,confirmation.Justification,true,cancelamento.Token);
            await ConsultarAsync();retryChanged?.Invoke();
            status.Text=receipt.State switch{
                "QUEUED"=>"Recuperação REST enfileirada após COMMIT; aguarde o resultado do motor. Não confirma sucesso Guardian.",
                "UNKNOWN"=>"Resultado do comando indeterminado. Não repetir: consulte o journal e reconcilie.",
                _=>"Recuperação REST bloqueada — "+receipt.Code};
            if(receipt.Accepted)
            {
                var until=DateTime.UtcNow.AddMinutes(1);
                while(!cancelamento.IsCancellationRequested&&DateTime.UtcNow<until)
                {
                    await Task.Delay(2000,cancelamento.Token);await ConsultarAsync();retryChanged?.Invoke();
                    var observed=linhas.SingleOrDefault(r=>r.Origemlinha.IntegracaoId==row.IntegracaoId)?.Origemlinha;
                    if(observed is null){status.Text="Leitura de acompanhamento indisponível. Não reenviar; consulte o journal.";break;}
                    if(observed.EstadoMotor is not("AGUARDANDO_REPROCESSAMENTO" or "EM_PROCESSAMENTO"))
                    {status.Text="Resultado persistido: "+observed.EstadoMotor+". Confira protocolo e journal.";break;}
                }
            }
        }
        catch(OperationCanceledException) when(cancelamento.IsCancellationRequested){}
        catch{if(!IsDisposed)status.Text="Não foi possível comprovar o resultado. Não repetir automaticamente; consulte o journal.";}
        finally{commandBusy=false;if(!IsDisposed){atualizar.Enabled=true;grid.Invalidate();}}
    }
}
