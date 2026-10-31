using FugaTES.Aplicacao.Transportadoras;
using FugaTES.WinForms.Transportadoras;

namespace FugaTES.WinForms;

public partial class FrmTransportadoras : Form
{
    private readonly CarrierSyncPresenter presenter;
    private readonly ICarrierSyncManualCommandClient manual;
    private readonly IDisposable? lifetime;
    private readonly Func<CarrierDesktopStatus>? desktopState;
    private readonly CancellationTokenSource cancellation = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 30000 };
    private bool refreshing;
    private bool serviceActive;
    private readonly HashSet<string> pendingRequests = [];
    public FrmTransportadoras(CarrierSyncPresenter presenter, ICarrierSyncManualCommandClient manual, IDisposable? lifetime = null, Func<CarrierDesktopStatus>? desktopState = null)
    {
        this.presenter = presenter; this.manual = manual; this.lifetime = lifetime; this.desktopState=desktopState;
        InitializeComponent();
        update.Click += async (_, _) => await RefreshAsync();
        detail.Click += async (_, _) => await DetailAsync();
        syncNow.Click += async (_, _) => await RequestAsync();
        logs.Click += (_, _) => tabs.SelectedTab = logsTab;
        cycles.SelectionChanged += async (_, _) => await LoadStepsAsync();
        rows.SelectionChanged += (_, _) => UpdateManualButton();
        Shown += async (_, _) => { await RefreshAsync(); if (!cancellation.IsCancellationRequested) refreshTimer.Start(); };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        FormClosing += (_, _) => { refreshTimer.Stop(); cancellation.Cancel(); };
    }
    private CarrierSyncFiltro Filter() => new(Blank(bp.Text), Blank(name.Text), Blank(statusFilter.Text),
        from.Checked ? new DateTimeOffset(from.Value.ToUniversalTime()) : null,
        until.Checked ? new DateTimeOffset(until.Value.ToUniversalTime()) : null);
    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) || value == "TODOS" ? null : value.Trim();
    private async Task RefreshAsync()
    {
        if (refreshing || cancellation.IsCancellationRequested) return;
        CarrierSyncFiltro filter;
        try { filter = Filter(); filter.Validar(); }
        catch (ArgumentException) { message.Text = "Período inválido. A data inicial deve preceder a final."; return; }
        refreshing = true; update.Enabled = detail.Enabled = false; serviceActive = false; UpdateManualButton();
        message.Text = "Consultando histórico PostgreSQL...";
        try
        {
            var snapshot = await presenter.RefreshAsync(filter, cancellation.Token);
            if (snapshot is null || cancellation.IsCancellationRequested) return;
            var localState=desktopState?.Invoke();
            serviceActive = localState?.Active ?? snapshot.Indicators.ServiceActive;
            var i = snapshot.Indicators;
            indicators.Text = $"Sincronização no FugaTES: {(serviceActive ? "ATIVA" : "INATIVA")} | Intervalo: {i.IntervalSeconds}s | Última consulta: {Local(localState?.LastQueryUtc ?? i.LastCycleUtc)} | Próxima consulta: {Local(localState is null ? i.NextCycleUtc : localState.NextQueryUtc)}\r\nSAP (histórico): {i.Found} | Guardian (confirmadas): {i.GuardianCount} | Criadas: {i.Created} | Atualizadas: {i.Updated} | Sem alteração: {i.Unchanged} | Pendentes: {i.Pending} | Erros: {i.Errors}";
            rows.DataSource = snapshot.Rows.Select(r => new Row(r.Bp,r.Name,r.GuardianCode ?? "",r.Status,Local(r.SapCheckedUtc),Local(r.GuardianSyncedUtc),r.Action,r.ResultCode)).ToArray();
            var values = await presenter.CyclesAsync(filter, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            cycles.DataSource = values.Select(c => new CycleRow(c.Id,Local(c.StartedUtc),Local(c.FinishedUtc), c.FinishedUtc.HasValue ? (c.FinishedUtc.Value-c.StartedUtc).TotalSeconds.ToString("F1") : "Em andamento",c.Consulted,c.Created,c.Updated,c.Unchanged,c.Pending,c.Failed,c.Status)).ToArray();
            message.Text = localState?.Code is {} code ? "Sincronização local: "+code : $"{snapshot.Rows.Count} transportadoras exibidas / limite {filter.Limit}. Horários locais ({TimeZoneInfo.Local.DisplayName}).";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { if (!cancellation.IsCancellationRequested) { serviceActive = false; rows.DataSource = null; cycles.DataSource = null; steps.DataSource = null; indicators.Text = "Sincronização: NÃO VALIDADA | Intervalo: 30s"; message.Text = "Leitura indisponível: confira conexão PostgreSQL, schema Transportadoras aprovado pela GAIA e permissões de consulta."; } }
        finally { refreshing = false; if (!cancellation.IsCancellationRequested && !IsDisposed) { update.Enabled = detail.Enabled = true; UpdateManualButton(); } }
    }
    private async Task LoadStepsAsync()
    {
        if (cycles.CurrentRow?.DataBoundItem is not CycleRow cycle || cancellation.IsCancellationRequested) return;
        var selected = cycle.Id;
        try
        {
            var values = await presenter.StepsAsync(selected,cancellation.Token);
            if (cancellation.IsCancellationRequested || cycles.CurrentRow?.DataBoundItem is not CycleRow current || current.Id != selected) return;
            steps.DataSource = values.Select(s => new StepRow(s.Bp,s.Step,s.ResultCode,Local(s.OccurredUtc))).ToArray();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { if (!cancellation.IsCancellationRequested) message.Text = "Não foi possível consultar as etapas deste ciclo."; }
    }
    private async Task DetailAsync()
    {
        if (rows.CurrentRow?.DataBoundItem is not Row row) { message.Text = "Selecione uma transportadora."; return; }
        try
        {
            var value = await presenter.DetailAsync(row.Bp,cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            if (value.State is null) { message.Text = "Transportadora não encontrada no histórico persistente."; return; }
            var text = $"BP SAP: {value.State.Bp}\nNome: {value.State.Name}\nGuardian: {value.State.GuardianCode ?? "Não resolvido"}\nStatus: {value.State.Status}\nAção: {value.State.Action}\nResultado: {value.State.ResultCode}\nConsulta SAP: {Local(value.State.SapCheckedUtc)}\nSincronização Guardian: {Local(value.State.GuardianSyncedUtc)}\n\n" + string.Join("\n", value.Steps.Select(s => $"{Local(s.OccurredUtc)} {s.Step}: {s.ResultCode}"));
            MessageBox.Show(this,text,"Detalhe da transportadora — horários locais",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { if (!cancellation.IsCancellationRequested) message.Text = "Não foi possível consultar o detalhe desta transportadora."; }
    }
    private void UpdateManualButton() => syncNow.Enabled = !refreshing && serviceActive && rows.CurrentRow?.DataBoundItem is Row r && !pendingRequests.Contains(r.Bp);
    private async Task RequestAsync()
    {
        if (!syncNow.Enabled || rows.CurrentRow?.DataBoundItem is not Row row || !pendingRequests.Add(row.Bp)) return;
        UpdateManualButton(); var requestId = Guid.NewGuid();
        try
        {
            var result = await manual.RequestAsync(requestId,row.Bp,cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            message.Text = result.Accepted ? $"Pedido {requestId} aceito pelo FugaTES. Aguarde resultado no histórico; sincronização ainda não concluída."
                : result.Indeterminate ? $"Resultado do pedido {requestId} indeterminado. Consulte os logs antes de solicitar novamente."
                : result.Code == "CARRIER_REQUEST_NOT_AUTHORIZED" ? "Pedido bloqueado: autorização operacional ausente ou vencida." : "Pedido bloqueado: sincronização local indisponível ou perfil HML ainda não liberado.";
            if (!result.Accepted && !result.Indeterminate) pendingRequests.Remove(row.Bp);
        }
        catch { if (!cancellation.IsCancellationRequested) message.Text = $"Resultado do pedido {requestId} desconhecido. Não repetir antes de conferir o histórico."; }
        finally { if (!IsDisposed && !cancellation.IsCancellationRequested) UpdateManualButton(); }
    }
    private bool runtimeDisposed;
    private void DisposeRuntime()
    {
        if (runtimeDisposed) return;
        runtimeDisposed = true; refreshTimer.Stop(); refreshTimer.Dispose(); cancellation.Cancel(); cancellation.Dispose(); lifetime?.Dispose();
    }
    private static string Local(DateTimeOffset? date) => date?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz") ?? "—";
    private sealed record Row(string Bp,string Name,string Guardian,string Status,string Sap,string Sync,string Action,string Result);
    private sealed record CycleRow(Guid Id,string Start,string End,string Duration,int Found,int Created,int Updated,int Unchanged,int Pending,int Errors,string Status);
    private sealed record StepRow(string Bp,string Step,string Result,string Time);
}
