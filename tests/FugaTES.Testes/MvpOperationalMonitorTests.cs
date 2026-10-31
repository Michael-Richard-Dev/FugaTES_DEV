using System.Text.Json;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.WinForms;
using FugaTES.WinForms.Monitor;

namespace FugaTES.Testes;

public sealed class MvpOperationalMonitorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projection_availability_preserves_INC005_and_maps_optional_fields(bool projection)
    {
        var row = Row() with { Fluxo = projection ? "MM_IN" : null, NumeroTicketGuardian = projection ? "RESPONSE-123" : null, TicketGuardian = "CALLBACK-999" };
        var repo = new Repository { Snapshot = new(projection, true, [row]) };
        var result = await new MonitorOperacionalConsulta(repo).ConsultarAsync(new(), CancellationToken.None);
        Assert.Equal(projection, result.ProjecaoDisponivel);
        Assert.Equal(row, Assert.Single(result.Integracoes));
        Assert.NotEqual(row.TicketGuardian, row.NumeroTicketGuardian);
    }

    [Fact]
    public void Read_model_maps_timestamps_attempt_and_guardian_weights_without_derivation()
    {
        var id = Guid.NewGuid();
        var row = JsonSerializer.Deserialize<MonitorIntegracao>($$"""{"IntegracaoId":"{{id}}","EstadoMotor":"EM_PROCESSAMENTO","CriadoEm":"2026-10-01T12:00:00+00:00","AtualizadoEm":"2026-10-01T12:00:01+00:00","Tentativa":"{{id}}"} """)!;
        Assert.Equal(id, row.IntegracaoId); Assert.Equal(id, row.Tentativa);
        Assert.True(row.AtualizadoEm > row.CriadoEm);
        var evt = JsonSerializer.Deserialize<MonitorEventoGuardian>("""{"EstadoCodigo":3,"Sequencial":123,"PesoBruto":null,"Tara":null,"PesoLiquido":null,"RecebidoEm":"2026-10-01T12:00:00Z"}""")!;
        Assert.Equal(3, evt.EstadoCodigo); Assert.Null(evt.Tara); Assert.Null(evt.PesoLiquido);
    }

    [Fact]
    public void Counters_preserve_operational_and_functional_meanings()
    {
        var states = new[] { "EM_PROCESSAMENTO", "AGUARDANDO_ACAO_MANUAL", "CONCLUIDA", "AGUARDANDO_REPROCESSAMENTO", "FALHA_DEFINITIVA", "RESULTADO_INDETERMINADO" };
        var rows = states.Select(s => Row() with { EstadoMotor = s }).ToList();
        rows.Add(Row() with { EstadoMotor = "CONCLUIDA", EstadoCorrelacao = "AGUARDANDO_TICKET" });
        var counters = MonitorOperacionalConsulta.Contadores(rows);
        Assert.Equal(7, counters.Count); Assert.All(counters.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task Filter_values_and_cancellation_are_forwarded_to_read_port()
    {
        var repo = new Repository(); var filter = new MonitorFiltro("MM_IN", "CONCLUIDA", "corr", "doc", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, "FALHAS");
        using var cts = new CancellationTokenSource();
        await new MonitorOperacionalConsulta(repo).ConsultarAsync(filter, cts.Token);
        Assert.Same(filter, repo.LastFilter); Assert.Equal(cts.Token, repo.Token);
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("'; DROP TABLE x;")]
    public async Task Unknown_group_is_rejected_before_repository(string group)
    {
        var repo = new Repository();
        await Assert.ThrowsAsync<ArgumentException>(() => new MonitorOperacionalConsulta(repo).ConsultarAsync(new(Grupo: group), CancellationToken.None));
        Assert.Equal(0, repo.Calls);
    }

    [Fact]
    public async Task Reversed_period_is_rejected_before_repository()
    {
        var repo = new Repository();
        await Assert.ThrowsAsync<ArgumentException>(() => new MonitorOperacionalConsulta(repo).ConsultarAsync(new(De: DateTimeOffset.UtcNow, Ate: DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None));
        Assert.Equal(0, repo.Calls);
    }

    [Theory]
    [InlineData("Bearer fake-fixture")]
    [InlineData("Password=fake-fixture")]
    [InlineData("Authorization: fake-fixture")]
    [InlineData("cookie=fake-fixture")]
    [InlineData("Host=fixture;Username=fixture")]
    [InlineData("assinatura=fake-fixture")]
    public async Task Unsafe_text_is_never_forwarded_to_view(string text)
    {
        var repo = new Repository { Snapshot = new(true, true, [Row() with { UltimoErro = text, CorrelationId = text, NumeroTicketGuardian = text }]) };
        var snapshot = await new MonitorOperacionalConsulta(repo).ConsultarAsync(new(), CancellationToken.None);
        Assert.Equal("[CONTEUDO OMITIDO]", snapshot.Integracoes[0].UltimoErro);
        Assert.Equal("[CONTEUDO OMITIDO]", snapshot.Integracoes[0].CorrelationId);
        Assert.Equal("[CONTEUDO OMITIDO]", snapshot.Integracoes[0].NumeroTicketGuardian);
    }

    [Fact]
    public async Task Detail_preserves_chronological_history_and_null_nonweight_events()
    {
        var now = DateTimeOffset.UtcNow;
        var repo = new Repository { Detail = new([], [new("claim", now, 1, null)], [new(1, 8, null, null, null, now), new(3, 8, null, null, null, now.AddMinutes(1)), new(5, 8, 100, 25, 75, now.AddMinutes(2))]) };
        var result = await new MonitorOperacionalConsulta(repo).DetalharAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(new[] { 1, 3, 5 }, result.Guardian.Select(e => e.EstadoCodigo));
        Assert.Null(result.Guardian[0].Tara); Assert.Equal(25, result.Guardian[2].Tara);
        Assert.Equal("claim", Assert.Single(result.Auditoria).Evento);
    }

    [Fact]
    public async Task Concurrent_refresh_is_skipped_and_later_refresh_is_allowed()
    {
        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repo = new Repository { Wait = wait.Task }; var view = new View();
        var presenter = new MonitorPresenter(new(repo), view);
        var first = presenter.AtualizarAsync(new(), null, CancellationToken.None);
        Assert.False(first.IsCompleted);
        Assert.False(await presenter.AtualizarAsync(new(), null, CancellationToken.None));
        Assert.Equal(1, repo.Calls); wait.SetResult(); Assert.True(await first);
        Assert.True(await presenter.AtualizarAsync(new(), null, CancellationToken.None)); Assert.Equal(2, view.Shown);
    }

    [Fact]
    public async Task Cancellation_on_close_suppresses_view_updates()
    {
        var repo = new Repository { Wait = Task.Delay(Timeout.Infinite) }; var view = new View();
        using var cts = new CancellationTokenSource();
        var task = new MonitorPresenter(new(repo), view).AtualizarAsync(new(), null, cts.Token);
        cts.Cancel(); Assert.False(await task); Assert.Equal(0, view.Shown); Assert.Equal(0, view.Errors);
    }

    [Fact]
    public async Task Repository_exception_is_sanitized_without_message_or_stack_trace()
    {
        var repo = new Repository { Fail = true }; var view = new View();
        Assert.False(await new MonitorPresenter(new(repo), view).AtualizarAsync(new(), null, CancellationToken.None));
        Assert.Equal(1, view.Errors); Assert.Equal(0, view.Shown);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Optional_table_sql_has_only_materially_available_references(bool projection, bool events)
    {
        var sql = MonitorPostgreSqlReadRepository.ConstruirSql(projection, events, true);
        Assert.Equal(projection, sql.Contains("projecao_resultado_fluxo_mvp"));
        Assert.Equal(events, sql.Contains("correlacao_guardian_evento"));
        Assert.Contains("@corr", sql); Assert.Contains("@doc", sql); Assert.Contains("@from", sql);
        Assert.DoesNotContain("diagnostico", sql); Assert.DoesNotContain("payload", sql);
        Assert.DoesNotContain("insert ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update ", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Controller_depends_on_application_query_not_concrete_database()
    {
        var parameters = typeof(MonitorPresenter).GetConstructors().Single().GetParameters();
        Assert.Equal(typeof(MonitorOperacionalConsulta), parameters[0].ParameterType);
        Assert.Equal(typeof(IMonitorView), parameters[1].ParameterType);
    }

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(980, 700)]
    public void Designer_readonly_grids_and_layout_render_at_supported_sizes(int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new FrmMonitorMvp(new(new Repository()), "TEST");
                form.ClientSize = new(width, height);
                form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                form.Location = new(-20000, -20000); form.ShowInTaskbar = false;
                form.Show(); System.Windows.Forms.Application.DoEvents(); form.PerformLayout();
                form.Mostrar(new(false, true, [Row()]));
                Assert.Equal(8.25f, form.Font.Size); Assert.Equal("Segoe UI", form.Font.Name);
                var controls = All(form).ToArray();
                Assert.Equal(5, controls.OfType<System.Windows.Forms.DataGridView>().Count());
                Assert.Equal(new[] { "Auditoria", "FluxoAcm", "Guardian", "Integracoes", "Tentativas" },
                    typeof(FrmMonitorMvp).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .Where(field => field.FieldType == typeof(System.Windows.Forms.DataGridView)).Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal));
                Assert.All(controls.OfType<System.Windows.Forms.DataGridView>(), grid => { Assert.True(grid.ReadOnly); Assert.False(grid.AllowUserToAddRows); Assert.False(grid.AllowUserToDeleteRows); });
                Assert.Contains(controls.OfType<System.Windows.Forms.Label>(), label => label.Text == "Projeção MVP ainda não instalada");
                using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new(0, 0, form.Width, form.Height));
                var colors = new HashSet<int>();
                for (var y = 0; y < height; y += 5)
                    for (var x = 0; x < width; x += 5) colors.Add(image.GetPixel(x, y).ToArgb());
                Assert.True(colors.Count > 12, "Monitor render must not be blank.");
                image.Save(Path.Combine(Path.GetTempPath(), $"FugaTES-Monitor-{width}x{height}.png"));
                form.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        Assert.Null(failure);
    }

    private static IEnumerable<System.Windows.Forms.Control> All(System.Windows.Forms.Control control)
    {
        foreach (System.Windows.Forms.Control child in control.Controls)
        { yield return child; foreach (var nested in All(child)) yield return nested; }
    }
    private static MonitorIntegracao Row() => new() { IntegracaoId = Guid.NewGuid(), Operacao = "MVP_MMIN101", EstadoMotor = "CONCLUIDA", CorrelationId = "MONITOR-TEST", Documento = "DOC-TEST", CriadoEm = DateTimeOffset.UtcNow, AtualizadoEm = DateTimeOffset.UtcNow };
    private sealed class Repository : IMonitorReadRepository
    {
        public int Calls; public MonitorFiltro? LastFilter; public CancellationToken Token;
        public MonitorSnapshot Snapshot = new(false, true, []); public MonitorDetalhe Detail = new([], [], []);
        public Task? Wait; public bool Fail;
        public async Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct)
        {
            Calls++; LastFilter = filtro; Token = ct;
            if (Fail) throw new InvalidOperationException("Password=fake-fixture");
            if (Wait is not null) await Wait.WaitAsync(ct);
            return Snapshot;
        }
        public Task<MonitorDetalhe> DetalharAsync(Guid id, CancellationToken ct) => Task.FromResult(Detail);
    }
    private sealed class View : IMonitorView
    {
        public int Shown, Errors;
        public void Mostrar(MonitorSnapshot snapshot) => Shown++;
        public void MostrarDetalhe(Guid id, MonitorDetalhe detalhe) { }
        public void MostrarFalha() => Errors++;
    }
}
