using FugaTES.Aplicacao.Transportadoras;
using FugaTES.WinForms.Transportadoras;

namespace FugaTES.Testes;

public sealed class CarrierSyncWinFormsTests
{
    [Fact]
    public async Task Invalid_period_is_blocked_before_query()
    {
        var repository = new Repository();
        var presenter = new CarrierSyncPresenter(repository);
        await Assert.ThrowsAsync<ArgumentException>(() => presenter.RefreshAsync(new(DeUtc: DateTimeOffset.UtcNow, AteUtc: DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None));
        Assert.Equal(0, repository.Calls);
    }
    [Fact]
    public async Task Filters_are_forwarded_and_result_messages_are_sanitized()
    {
        var repository = new Repository();
        var presenter = new CarrierSyncPresenter(repository);
        var filter = new CarrierSyncFiltro("0009", "Transportes", "BLOCKED_DATA", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow);
        var snapshot = await presenter.RefreshAsync(filter, CancellationToken.None);
        Assert.Equal(filter, repository.Filter);
        Assert.Equal("0009", Assert.Single(snapshot!.Rows).Bp);
        Assert.Equal("[CONTEUDO OMITIDO]", snapshot.Rows[0].ResultCode);
        Assert.False(snapshot.Indicators.ServiceActive);
    }
    [Fact]
    public async Task Overlapping_refresh_is_not_queued()
    {
        var repository = new Repository { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var presenter = new CarrierSyncPresenter(repository);
        var first = presenter.RefreshAsync(new(), CancellationToken.None);
        Assert.Null(await presenter.RefreshAsync(new(), CancellationToken.None));
        repository.Gate.SetResult();
        await first;
        Assert.Equal(1, repository.Calls);
    }
    [Fact]
    public async Task Failed_refresh_releases_guard_and_does_not_return_fake_data()
    {
        var repository = new Repository { Fail = true };
        var presenter = new CarrierSyncPresenter(repository);
        await Assert.ThrowsAsync<InvalidOperationException>(() => presenter.RefreshAsync(new(), CancellationToken.None));
        repository.Fail = false;
        Assert.NotNull(await presenter.RefreshAsync(new(), CancellationToken.None));
    }
    [Fact]
    public void Manual_timeout_is_indeterminate_and_not_accepted()
    {
        var request = Guid.NewGuid();
        var result = CarrierManualResult.Unknown(request);
        Assert.False(result.Accepted);
        Assert.True(result.Indeterminate);
        Assert.Equal(request, result.RequestId);
    }
    [Fact]
    public void Forms_construct_with_navigation_logs_readonly_grids_and_controlled_action()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var navigation = new FugaTES.WinForms.FrmIntegracoes();
                Assert.Contains(All(navigation).OfType<System.Windows.Forms.Button>(), b => b.Text == "Contratos ACM");
                Assert.Contains(All(navigation).OfType<System.Windows.Forms.Button>(), b => b.Text == "Transportadoras");
                using var form = new FugaTES.WinForms.FrmTransportadoras(new(new Repository()), new Manual());
                var controls = All(form).ToArray();
                Assert.Equal(3, controls.OfType<System.Windows.Forms.DataGridView>().Count());
                Assert.All(controls.OfType<System.Windows.Forms.DataGridView>(), g => Assert.True(g.ReadOnly));
                Assert.Contains(controls.OfType<System.Windows.Forms.TabPage>(), t => t.Text == "Logs de Sincronização");
                Assert.False(Assert.Single(controls.OfType<System.Windows.Forms.Button>(), b => b.Text.StartsWith("Sincronizar agora")).Enabled);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control control)
    {
        foreach (System.Windows.Forms.Control child in control.Controls)
        {
            yield return child;
            foreach (var value in All(child)) yield return value;
        }
    }
    private sealed class Manual : ICarrierSyncManualCommandClient
    {
        public Task<CarrierManualResult> RequestAsync(Guid id,string bp,CancellationToken ct) => throw new InvalidOperationException("NOT_EXPECTED");
    }
    private sealed class Repository : ICarrierSyncReadRepository
    {
        public int Calls; public CarrierSyncFiltro? Filter; public TaskCompletionSource? Gate; public bool Fail;
        public async Task<CarrierSyncSnapshot> ConsultarAsync(CarrierSyncFiltro filter, CancellationToken ct)
        {
            Calls++; Filter = filter;
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            if (Fail) throw new InvalidOperationException("DATABASE_UNAVAILABLE");
            return new([new("0009", "Transportes", null, new string('A',64), "BLOCKED_DATA", "NO_ACTION", "Bearer secret", DateTimeOffset.UtcNow, null, null)], new(false,30,null,null,1,0,0,0,1,0));
        }
        public Task<CarrierBpDetail> DetalharBpAsync(string bp, CancellationToken ct) => Task.FromResult(new CarrierBpDetail(null, []));
        public Task<IReadOnlyList<CarrierStoredCycle>> ConsultarCiclosAsync(CarrierSyncFiltro filtro,CancellationToken ct) => Task.FromResult<IReadOnlyList<CarrierStoredCycle>>([]);
        public Task<IReadOnlyList<CarrierStoredStep>> DetalharCicloAsync(Guid id,CancellationToken ct) => Task.FromResult<IReadOnlyList<CarrierStoredStep>>([]);
    }
}
