using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;

namespace FugaTES.WinForms.Monitor;

public interface IMonitorView
{
    void Mostrar(MonitorSnapshot snapshot);
    void MostrarDetalhe(Guid id, MonitorDetalhe detalhe);
    void MostrarFalha();
    void MostrarAcm(Guid id, MonitorAcmDados dados) { }
}

public sealed class MonitorPresenter(MonitorOperacionalConsulta consulta, IMonitorView view,
    Func<Guid, CancellationToken, Task<MonitorAcmDetalhe>>? lerAcm = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<bool> AtualizarAsync(MonitorFiltro filtro, Guid? selectedId, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return false;
        try
        {
            var snapshot = await consulta.ConsultarAsync(filtro, ct);
            var id = selectedId is Guid selected && snapshot.Integracoes.Any(i => i.IntegracaoId == selected)
                ? selectedId : snapshot.Integracoes.FirstOrDefault()?.IntegracaoId;
            MonitorAcmDetalhe? acm = id is Guid acmId && lerAcm is not null ? await lerAcm(acmId, ct) : null;
            MonitorDetalhe? detail = acm?.Detalhe ?? (id is Guid detailId ? await consulta.DetalharAsync(detailId, ct) : null);
            ct.ThrowIfCancellationRequested();
            view.Mostrar(snapshot);
            if (detail is not null) view.MostrarDetalhe(id!.Value, detail);
            if (acm is not null) view.MostrarAcm(id!.Value, acm.Acm.Sanitizar());
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch { if (!ct.IsCancellationRequested) view.MostrarFalha(); return false; }
        finally { gate.Release(); }
    }

    public async Task DetalharAsync(Guid id, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) return;
        try
        {
            var acm = lerAcm is null ? null : await lerAcm(id, ct);
            var detail = acm?.Detalhe ?? await consulta.DetalharAsync(id, ct);
            ct.ThrowIfCancellationRequested(); view.MostrarDetalhe(id, detail);
            if (acm is not null) view.MostrarAcm(id, acm.Acm.Sanitizar());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { if (!ct.IsCancellationRequested) view.MostrarFalha(); }
        finally { gate.Release(); }
    }
}
