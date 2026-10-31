using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Aplicacao.Transportadoras;

namespace FugaTES.WinForms.Transportadoras;

public sealed class CarrierSyncPresenter(ICarrierSyncReadRepository repository)
{
    private int loading;
    public async Task<CarrierSyncSnapshot?> RefreshAsync(CarrierSyncFiltro filter, CancellationToken ct)
    {
        filter.Validar();
        if (Interlocked.CompareExchange(ref loading, 1, 0) != 0) return null;
        try
        {
            var snapshot = await repository.ConsultarAsync(filter, ct);
            return snapshot with { Rows = snapshot.Rows.Select(r => r with { Name = Safe(r.Name), Status = Safe(r.Status), Action = Safe(r.Action), ResultCode = Safe(r.ResultCode), GuardianCode = r.GuardianCode is null ? null : Safe(r.GuardianCode) }).ToArray() };
        }
        finally { Interlocked.Exchange(ref loading, 0); }
    }
    public Task<IReadOnlyList<CarrierStoredCycle>> CyclesAsync(CarrierSyncFiltro filter, CancellationToken ct) { filter.Validar(); return repository.ConsultarCiclosAsync(filter, ct); }
    public async Task<CarrierBpDetail> DetailAsync(string bp, CancellationToken ct)
    {
        CarrierPersistencePolicy.ValidateBp(bp);
        var value = await repository.DetalharBpAsync(bp, ct);
        return value with { State = value.State is null ? null : value.State with { Name = Safe(value.State.Name), ResultCode = Safe(value.State.ResultCode) }, Steps = Sanitize(value.Steps) };
    }
    public async Task<IReadOnlyList<CarrierStoredStep>> StepsAsync(Guid cycleId, CancellationToken ct) => Sanitize(await repository.DetalharCicloAsync(cycleId, ct));
    private static IReadOnlyList<CarrierStoredStep> Sanitize(IReadOnlyList<CarrierStoredStep> values) => values.Select(v => v with { Step = Safe(v.Step), ResultCode = Safe(v.ResultCode) }).ToArray();
    private static string Safe(string value) => MonitorOperacionalConsulta.TextoSeguro(value) ?? "";
}
