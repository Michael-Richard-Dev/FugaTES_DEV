using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Persistencia.Transportadoras;
using Npgsql;

namespace FugaTES.WinForms.Transportadoras;

internal static class CarrierSyncComposition
{
    public static FrmTransportadoras Criar()
    {
        NpgsqlDataSource? source = null;
        ICarrierSyncReadRepository repository = new Disconnected();
        if (Runtime.DesktopRuntime.Current is { } runtime)
        {
            source = runtime.CreateReadSource();
            repository = new CarrierSyncReadRepository(source);
        }
        return new(new CarrierSyncPresenter(repository), new CarrierDesktopManualClient(CarrierDesktopComposition.Current), source, () => CarrierDesktopComposition.Current?.Snapshot ?? new(false,false,null,null,"CARRIER_DESKTOP_NOT_READY"));
    }
    private sealed class Disconnected : ICarrierSyncReadRepository
    {
        private static InvalidOperationException Failure() => new("CARRIER_DATABASE_UNAVAILABLE");
        public Task<CarrierSyncSnapshot> ConsultarAsync(CarrierSyncFiltro filtro, CancellationToken ct) => throw Failure();
        public Task<CarrierBpDetail> DetalharBpAsync(string bp, CancellationToken ct) => throw Failure();
        public Task<IReadOnlyList<CarrierStoredCycle>> ConsultarCiclosAsync(CarrierSyncFiltro filtro, CancellationToken ct) => throw Failure();
        public Task<IReadOnlyList<CarrierStoredStep>> DetalharCicloAsync(Guid id, CancellationToken ct) => throw Failure();
    }
}
