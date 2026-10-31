using System.Drawing;
using System.Windows.Forms;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.WinForms;
using FugaTES.WinForms.Monitor;

namespace FugaTES.Testes;

public sealed class MonitorCandidateGuiTests
{
    [Fact]
    public void Packaged_candidate_is_inert_and_never_selects_transport_policy()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AcmMonitor.R165M.json"));
        var profile = System.Text.Json.JsonSerializer.Deserialize<AcmMonitorProfile>(json)!;
        Assert.False(profile.Enabled); Assert.False(profile.Valido);
        Assert.Equal(15433, profile.LocalPort); Assert.Equal("FugaTES/HML/ACM/PostgreSQL", profile.CredentialTarget);
        Assert.Empty(profile.TransportSslMode); Assert.Empty(profile.TransportPolicyReference); Assert.Empty(profile.ClearanceReference);
        var calls = 0;
        using var session = AcmMonitorSession.Criar(profile, (_, _) => { calls++; throw new Exception("SHOULD-NOT-BE-CALLED"); });
        Assert.Equal(0, calls); Assert.False(session.Configurado);
    }

    [Theory]
    [InlineData("Prefer")]
    [InlineData("Allow")]
    [InlineData("")]
    [InlineData("Unrecognized")]
    public void Fallback_or_unknown_transport_is_rejected_before_provider(string mode)
    {
        var calls = 0;
        using var session = AcmMonitorSession.Criar(new()
        {
            Enabled = true, LocalPort = 15433, CredentialTarget = "FugaTES/HML/ACM/PostgreSQL", ClearanceReference = "SYNTHETIC",
            TransportSslMode = mode, TransportPolicyReference = "SYNTHETIC-ONLY-NOT-AUTHORIZATION"
        }, (_, _) => { calls++; return "SYNTHETIC-ONLY"; });
        Assert.Equal(0, calls); Assert.False(session.Configurado);
    }

    [Theory]
    [InlineData(5432, "FugaTES/HML/ACM/PostgreSQL")]
    [InlineData(5433, "FugaTES/HML/ACM/PostgreSQL")]
    [InlineData(15432, "FugaTES/HML/ACM/PostgreSQL")]
    [InlineData(15434, "FugaTES/HML/ACM/PostgreSQL")]
    [InlineData(15433, "SYNTHETIC-WRONG-TARGET")]
    public void Exact_ACM_endpoint_and_target_are_required_before_provider(int port, string target)
    {
        var calls = 0;
        using var session = AcmMonitorSession.Criar(new()
        {
            Enabled = true, LocalPort = port, CredentialTarget = target, ClearanceReference = "SYNTHETIC",
            TransportSslMode = "Require", TransportPolicyReference = "SYNTHETIC-ONLY-NOT-AUTHORIZATION"
        }, (_, _) => { calls++; return "SYNTHETIC-ONLY"; });
        Assert.Equal(0, calls); Assert.False(session.Configurado);
    }

    [Fact]
    public void Enabled_profile_without_explicit_transport_policy_is_blocked_before_provider()
    {
        var calls = 0;
        using var session = AcmMonitorSession.Criar(new()
        {
            Enabled = true, LocalPort = 15433, CredentialTarget = "FugaTES/HML/ACM/PostgreSQL", ClearanceReference = "SYNTHETIC-ONLY"
        }, (_, _) => { calls++; return "SYNTHETIC-ONLY"; });
        Assert.Equal(0, calls);
        Assert.False(session.Configurado);
    }

    [Theory]
    [InlineData(1280, 800, false)]
    [InlineData(980, 700, false)]
    [InlineData(1280, 800, true)]
    [InlineData(980, 700, true)]
    public void Existing_screen_shows_actual_error_and_independent_durable_identifiers(int width, int height, bool metadataU)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
                var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
                var row = new MonitorIntegracao { IntegracaoId = id, CorrelationId = "SYNTHETIC-CORRELATION", Documento = "SYNTHETIC-1155", Operacao = "SAP_CONTRATO_TOLEDO", EstadoMotor = "RESULTADO_INDETERMINADO", CriadoEm = now, AtualizadoEm = now, UltimoErro = "SYNTHETIC-GUARDIAN-ERROR" };
                if (metadataU) row = row with { Fluxo = "ACM_IN", StatusFuncional = "RESULTADO_INDETERMINADO", EtapaFuncional = "R165U_REST_ARMED", UltimoResultado = $"U:{new string('A', 64)}:{new string('B', 64)}:582" };
                using var form = new FrmMonitorMvp(new(new EmptyRepository()), "SYNTHETIC ONLY - OFFLINE");
                form.Text = "R165Y - TESTE SINTETICO - SEM BANCO";
                form.ClientSize = new(width, height); form.Location = new(-20000, -20000); form.StartPosition = FormStartPosition.Manual;
                form.ShowInTaskbar = false; form.Show(); Application.DoEvents();
                form.Mostrar(new(false, false, [row]));
                form.MostrarDetalhe(id, new([], [], []));
                form.MostrarAcm(id, new() { ContratosDisponiveis = true, Contrato = "SYNTHETIC-1155", CodigoGuardian = "001155", Referencia = "SYNTHETIC-REF", Sequencial = "00029", RecebidoEm = now });
                var controls = All(form).ToArray();
                var text = string.Join("\n", controls.OfType<TextBox>().Select(x => x.Text));
                Assert.Contains("SYNTHETIC-GUARDIAN-ERROR", text);
                Assert.Contains("RESULTADO_INDETERMINADO", text);
                Assert.Contains("SYNTHETIC-CORRELATION", text);
                Assert.Contains(id.ToString(), text);
                Assert.Contains("001155", text);
                Assert.Contains("Sequencial callback: 00029", text);
                Assert.Contains("Sequencial contrato: " + (metadataU ? "582" : "-"), text);
                if (metadataU) Assert.Contains("FORMATO_E_ESTADO_VALIDOS", text);
                Assert.Contains("Protocolo: -", text);
                Assert.Contains("2026-10-25", text);
                Assert.All(controls.OfType<DataGridView>(), grid => Assert.True(grid.ReadOnly));
                form.PerformLayout();
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new(0, 0, form.Width, form.Height));
                var colors = new HashSet<int>();
                for (var y = 0; y < image.Height; y += 5)
                    for (var x = 0; x < image.Width; x += 5) colors.Add(image.GetPixel(x, y).ToArgb());
                Assert.True(colors.Count > 12, "Synthetic monitor render must not be blank.");
                image.Save(Path.Combine(AppContext.BaseDirectory, $"R165Y-SYNTHETIC-{width}x{height}-U{metadataU}.png"));
                form.MostrarFalha();
                Assert.DoesNotContain("001155", string.Join("\n", All(form).OfType<TextBox>().Select(x => x.Text)));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        Assert.Null(failure);
    }

    private static IEnumerable<Control> All(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; }
    }

    private sealed class EmptyRepository : IMonitorReadRepository
    {
        public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct) => throw new InvalidOperationException("SYNTHETIC-NOT-CALLED");
        public Task<MonitorDetalhe> DetalharAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException("SYNTHETIC-NOT-CALLED");
    }
}
