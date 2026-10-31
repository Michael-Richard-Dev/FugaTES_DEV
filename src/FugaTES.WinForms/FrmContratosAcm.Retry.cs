using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.WinForms.ManualRetry;

namespace FugaTES.WinForms;

public partial class FrmContratosAcm
{
    AcmManualRetryController retry=null!;
    readonly Dictionary<Guid,AcmRetryPreview> retryPreviews=new();
    Action? retryStateChanged;
    bool retryBusy;
    internal object ExportRetryReadiness()=>new{commandConfigured=retry.Configured,
        rows=grid.Rows.Cast<DataGridViewRow>().Select(r=>r.DataBoundItem).OfType<ContratoAcmLeitura>().Select(r=>
        {retryPreviews.TryGetValue(r.IntegracaoId,out var p);return new{r.IntegracaoId,r.NumeroContrato,r.Item,r.EstadoIntegracao,
            canRequest=retry.Configured&&p?.Decision.Eligible==true,code=p?.Decision.Code,reason=p?.Decision.Message};}).ToArray(),message=status.Text};
    internal async Task InspectRetryForEvidenceAsync(Guid id)
    {
        // Observation is structurally GET-only, even if a future server returns an eligible preview.
        // Never invoke the Reenviar handler, confirmation dialog or Submit from the UI verifier.
        var row=grid.Rows.Cast<DataGridViewRow>().Select(r=>r.DataBoundItem).OfType<ContratoAcmLeitura>().Single(r=>r.IntegracaoId==id);
        var preview=await retry.InspectAsync(row,cancelamento.Token);
        retryPreviews[row.IntegracaoId]=preview;
        status.Text="Prévia somente leitura — "+preview.Decision.Message;
        grid.InvalidateColumn(grid.Columns["reenviar"]!.Index);
    }
    void ConfigureRetry(IAcmManualRetryCommandClient? client,Action? changed,AcmManualRetryController? shared=null)
    {
        retry=shared??new(client??new ReadOnlyAcmManualRetryClient());retryStateChanged=changed;
        grid.Columns.Add(new DataGridViewButtonColumn{Name="reenviar",HeaderText="Reenvio",Text="Reenviar",UseColumnTextForButtonValue=true,
            Width=80,AutoSizeMode=DataGridViewAutoSizeColumnMode.None});
        grid.Columns["reenviar"]!.DisplayIndex=1;
        grid.CellFormatting+=(_,e)=>
        {
            if(e.RowIndex<0||grid.Columns[e.ColumnIndex].Name!="reenviar")return;
            var row=grid.Rows[e.RowIndex].DataBoundItem as ContratoAcmLeitura;
            var enabled=!retryBusy&&retry.Configured&&row is not null&&retryPreviews.TryGetValue(row.IntegracaoId,out var p)&&p.Decision.Eligible;
            e.CellStyle!.ForeColor=enabled?SystemColors.ControlText:SystemColors.GrayText;
        };
        grid.CellContentClick+=async(_,e)=>
        {if(e.RowIndex>=0&&e.ColumnIndex>=0&&grid.Columns[e.ColumnIndex].Name=="reenviar"&&grid.Rows[e.RowIndex].DataBoundItem is ContratoAcmLeitura row)await ReenviarAsync(row);};
        grid.SelectionChanged+=(_,_)=>
        {
            if(!carregando&&!retryBusy&&grid.CurrentRow?.DataBoundItem is ContratoAcmLeitura row&&retryPreviews.TryGetValue(row.IntegracaoId,out var p))
                status.Text="Reenvio — "+p.Decision.Message;
        };
    }
    async Task AtualizarReenviosAsync(IReadOnlyList<ContratoAcmLeitura> rows)
    {
        retryPreviews.Clear();
        foreach(var row in rows)
        {
            var preview=await retry.InitialPreviewAsync(row,cancelamento.Token);
            if(cancelamento.IsCancellationRequested||IsDisposed)return;
            retryPreviews[row.IntegracaoId]=preview;
            foreach(DataGridViewRow gridRow in grid.Rows)
                if(gridRow.DataBoundItem is ContratoAcmLeitura r&&r.IntegracaoId==row.IntegracaoId)
                    gridRow.Cells["reenviar"].ToolTipText=preview.Decision.Message;
        }
        grid.InvalidateColumn(grid.Columns["reenviar"]!.Index);
        status.Text=$"{rows.Count} versão(ões) persistida(s). Reenvio: "+(retry.Configured?"sujeito ao preflight e autorização unitária.":"BLOQUEADO — serviço de comando não homologado. Passe o mouse em Reenviar para o motivo por integração.");
    }
    async Task ReenviarAsync(ContratoAcmLeitura row)
    {
        if(retryBusy||carregando)return;
        retryBusy=true;atualizar.Enabled=false;grid.Invalidate();
        try
        {
            var preview=await retry.InspectAsync(row,cancelamento.Token);
            retryPreviews[row.IntegracaoId]=preview;
            if(!retry.Configured||!preview.Decision.Eligible){status.Text="Reenvio bloqueado — "+preview.Decision.Message;return;}
            using var confirmation=new AcmRetryConfirmation(preview);
            if(confirmation.ShowDialog(this)!=DialogResult.OK)return;
            var result=await retry.SubmitAsync(row,preview,confirmation.Justification,true,cancelamento.Token);
            var message=result.State switch {"QUEUED"=>"Comando enfileirado após COMMIT; isso não confirma sucesso Guardian.",
                "UNKNOWN"=>"Resultado do comando indeterminado. Não repetir; reconciliar journal e tentativas.",_=>"Comando bloqueado: "+result.Code};
            await ConsultarAsync();retryStateChanged?.Invoke();status.Text=message;
            if(result.Accepted)
            {
                // Observe only; never resubmit the command while waiting for the independent worker.
                var until=DateTime.UtcNow.AddMinutes(1);
                while(!cancelamento.IsCancellationRequested&&DateTime.UtcNow<until)
                {
                    var observed=grid.Rows.Cast<DataGridViewRow>().Select(r=>r.DataBoundItem).OfType<ContratoAcmLeitura>().SingleOrDefault(r=>r.IntegracaoId==row.IntegracaoId);
                    if(observed is null){status.Text="Acompanhamento indisponível; consulte o journal. Não reenviar.";break;}
                    if(observed.EstadoIntegracao is not("AGUARDANDO_REPROCESSAMENTO" or "EM_PROCESSAMENTO"))
                    {status.Text="Resultado persistido do motor: "+observed.EstadoIntegracao+". Confira etapa/journal no monitor.";break;}
                    await Task.Delay(2000,cancelamento.Token);await ConsultarAsync();retryStateChanged?.Invoke();
                }
            }
        }
        catch(OperationCanceledException) when(cancelamento.IsCancellationRequested){}
        catch{status.Text="Não foi possível comprovar o resultado do comando. Não reenviar automaticamente.";}
        finally{retryBusy=false;if(!IsDisposed){atualizar.Enabled=true;grid.Invalidate();}}
    }
}

