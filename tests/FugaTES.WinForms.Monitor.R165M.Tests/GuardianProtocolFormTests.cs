using System.Windows.Forms;
using FugaTES.WinForms;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.WinForms.Monitor;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
using static FugaTES.Testes.GuardianProtocolClientTests;

namespace FugaTES.Testes;

public sealed class GuardianProtocolFormTests
{
    [Fact]
    public void Protocol_tab_starts_readonly_with_send_locked_and_visible_reason()
    {
        OnSta(() =>
        {
            using var form = new FrmMonitorMvp();
            var tab = Descendants(form).OfType<TabPage>().SingleOrDefault(t => t.Text == "PROTOCOLO GUARDIAN");
            Assert.NotNull(tab);
            var json = Assert.IsType<TextBox>(Descendants(tab).Single(c => c.Name == "ProtocolJson"));
            Assert.True(json.ReadOnly);
            Assert.True(json.WordWrap);
            Assert.Equal(ScrollBars.Vertical, json.ScrollBars);
            Assert.Equal("", json.Text);
            var send = Assert.IsType<Button>(Descendants(tab).Single(c => c.Name == "SendProtocol"));
            Assert.False(send.Enabled);
            var status = Descendants(tab).Single(c => c.Name == "ProtocolLockReason");
            Assert.False(string.IsNullOrWhiteSpace(status.Text));
        });
    }

    [Fact]
    public void Explicit_preview_binds_headers_result_and_exact_copy_without_calls_on_selection_or_refresh()
    {
        OnSta(() =>
        {
            var gets = 0;
            var posts = 0;
            var confirmations = 0;
            string? copied = null;
            using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
            {
                if (request.Method == HttpMethod.Get) gets++; else posts++;
                return Reply(Snapshot() with { Endpoint = "https://guardian.invalid/protocolo?private-query=test", HttpStatus = 400, SafeBusinessMessage = "Rejeitado", SafeResponseBody = "{\"codigo\":400}" });
            }), () => Sid);
            using var form = CreateForm(client, _ => { confirmations++; return false; }, value => copied = value);
            Bind(form);
            Assert.Equal(0, gets);
            Assert.False(Control<Button>(form, "SendProtocol").Enabled);
            Click(Control<Button>(form, "GenerateProtocol"));
            PumpUntil(() => gets == 1 && Control<Button>(form, "CopyProtocol").Enabled);
            Assert.Equal(Payload, Control<TextBox>(form, "ProtocolJson").Text);
            var metadata = Control<TextBox>(form, "ProtocolMetadata").Text;
            foreach (var expected in new[] { "POST", "https://guardian.invalid/protocolo", Hash, "application/json", "GUARDIAN_HML_BACKEND", "SYNTHETIC-ONLY", "Host: guardian.invalid", "Content-Length (UTF-8): 49" })
                Assert.Contains(expected, metadata);
            Assert.DoesNotContain("private-query", metadata);
            var result = Control<TextBox>(form, "ProtocolResult").Text;
            Assert.Contains("400", result);
            Assert.Contains("Rejeitado", result);
            Assert.Contains("{\"codigo\":400}", result);
            Click(Control<Button>(form, "CopyProtocol"));
            Assert.Equal(Payload, copied);
            Assert.True(Control<Button>(form, "SendProtocol").Enabled);
            Click(Control<Button>(form, "SendProtocol"));
            Assert.Equal(1, confirmations);
            Assert.Equal(0, posts);
            Bind(form);
            Assert.Equal(1, gets);
            Assert.Equal(Payload, Control<TextBox>(form, "ProtocolJson").Text);
            Bind(form, Guid.NewGuid(), "OTHER");
            Assert.Equal("", Control<TextBox>(form, "ProtocolJson").Text);
            Assert.False(Control<Button>(form, "SendProtocol").Enabled);
        });
    }

    [Fact]
    public void Confirmed_form_click_sends_once_and_displays_backend_protocol_id()
    {
        OnSta(() =>
        {
            var posts = 0;
            using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
            {
                if (request.Method == HttpMethod.Get) return Reply(Snapshot());
                posts++;
                return Reply(Snapshot() with { CanSend = false, State = "SUCCEEDED", HttpStatus = 201, ProtocolId = 12345678901L });
            }), () => Sid);
            using var form = CreateForm(client, _ => true);
            Bind(form);
            Click(Control<Button>(form, "GenerateProtocol"));
            PumpUntil(() => Control<Button>(form, "SendProtocol").Enabled);
            Click(Control<Button>(form, "SendProtocol"));
            PumpUntil(() => Control<TextBox>(form, "ProtocolResult").Text.Contains("12345678901"));
            Click(Control<Button>(form, "SendProtocol"));
            Assert.Equal(1, posts);
            Assert.False(Control<Button>(form, "SendProtocol").Enabled);
            Assert.Contains("SEND_SUCCEEDED", Control<Label>(form, "ProtocolLockReason").Text);
        });
    }

    [Fact]
    public void Selection_change_during_confirmation_cannot_send_a_different_preview()
    {
        OnSta(() =>
        {
            using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Reply(Snapshot());
            }), () => Sid);
            FrmMonitorMvp? form = null;
            using (form = CreateForm(client, _ => { Bind(form!, Guid.NewGuid(), "OTHER"); return true; }))
            {
                Bind(form);
                Click(Control<Button>(form, "GenerateProtocol"));
                PumpUntil(() => Control<Button>(form, "SendProtocol").Enabled);
                Click(Control<Button>(form, "SendProtocol"));
                Assert.False(Control<Button>(form, "SendProtocol").Enabled);
            }
        });
    }

    [Fact]
    public void Historical_preview_is_usable_with_command_disabled_and_displays_missing_response_as_unavailable()
    {
        var phase = "starting";
        OnSta(() =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "form-protocol-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(Snapshot() with { HttpStatus = 400 }));
                using var session = GuardianProtocolSession.Create(new(), previewReadbackFile: path);
                string? copied = null;
                phase = "creating form";
                using var form = CreateForm(session.Client, _ => throw new Exception("NO_CONFIRMATION"), value => copied = value);
                phase = "binding";
                Bind(form);
                phase = "generating";
                Assert.True(Control<Button>(form, "GenerateProtocol").Enabled);
                Click(Control<Button>(form, "GenerateProtocol"));
                phase = "pumping preview";
                PumpUntil(() => Control<Button>(form, "CopyProtocol").Enabled);
                phase = "asserting preview";
                Assert.Contains("HISTORICAL_READ_ONLY", Control<TextBox>(form, "ProtocolMetadata").Text);
                phase = "checking lock label";
                Assert.Contains("COMMAND_CREDENTIAL_NOT_PROVISIONED", Control<Label>(form, "ProtocolLockReason").Text);
                phase = "reading result";
                var result = Control<TextBox>(form, "ProtocolResult").Text;
                phase = "checking result: " + result;
                Assert.Contains("400", result);
                Assert.Contains("Mensagem: -", result);
                Assert.Contains("Resposta sanitizada: -", result);
                Assert.False(Control<Button>(form, "SendProtocol").Enabled);
                phase = "clicking copy";
                Click(Control<Button>(form, "CopyProtocol"));
                phase = "copy complete";
                Assert.Equal(Payload, copied);
                Click(Control<Button>(form, "SendProtocol"));
                phase = "disposing";
            }
            finally { File.Delete(path); }
        }, () => phase);
    }

    [Theory]
    [InlineData(1280, 800)]
    [InlineData(980, 700)]
    public void Synthetic_protocol_DrawToBitmap_has_visible_json_without_button_overlap(int width, int height)
    {
        OnSta(() =>
        {
            var gets = 0;
            using var client = new GuardianProtocolClient(Profile(), "command-test", new Handler(request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                gets++;
                return Reply(Snapshot() with
                {
                    CanSend = false, BlockCode = "SYNTHETIC_VISUAL_QA_NO_SEND", HttpStatus = 400,
                    SafeBusinessMessage = "SYNTHETIC TEST FIXTURE - nenhuma operacao real",
                    SafeResponseBody = "{\"synthetic\":true,\"error\":\"VISUAL_QA_ONLY\"}"
                });
            }), () => Sid);
            using var form = new VisualFixtureForm(client);
            form.Text = "SYNTHETIC TEST FIXTURE - PROTOCOLO GUARDIAN";
            form.Size = new Size(width, height);
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            Descendants(form).OfType<CheckBox>().Single().Checked = false;
            form.Show();
            Application.DoEvents();
            Bind(form);
            var tab = Control<TabPage>(form, "ProtocolTab");
            Descendants(form).OfType<TabControl>().Single().SelectedTab = tab;
            Click(Control<Button>(form, "GenerateProtocol"));
            PumpUntil(() => Control<Button>(form, "CopyProtocol").Enabled);
            form.PerformLayout();
            foreach (var control in Descendants(form))
            {
                _ = control.Handle;
                control.PerformLayout();
            }
            Application.DoEvents();

            var json = Control<TextBox>(form, "ProtocolJson");
            Assert.Equal(Payload, json.Text);
            Assert.True(json.Width > 0 && json.Height > 0);
            Assert.True(json.Parent!.ClientRectangle.Contains(json.Bounds), $"JSON {json.Bounds}; parent {json.Parent.ClientRectangle}; tab {tab.Bounds}");
            Rectangle BoundsInForm(System.Windows.Forms.Control control)
            {
                var bounds = control.Bounds;
                for (var parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
                    bounds.Offset(parent.Location);
                return bounds;
            }
            var jsonBounds = BoundsInForm(json);
            Assert.True(form.ClientRectangle.Contains(jsonBounds), $"JSON {jsonBounds}; form client {form.ClientRectangle}");
            foreach (var name in new[] { "GenerateProtocol", "CopyProtocol", "SendProtocol" })
            {
                var button = Control<Button>(form, name);
                var bounds = BoundsInForm(button);
                Assert.True(bounds.Width > 0 && bounds.Height > 0);
                Assert.True(form.ClientRectangle.Contains(bounds));
                Assert.False(jsonBounds.IntersectsWith(bounds), $"ProtocolJson overlaps {name} at {width}x{height}");
            }
            Assert.False(Control<Button>(form, "SendProtocol").Enabled);
            Assert.Equal(1, gets);

            using var bitmap = new Bitmap(width, height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var colors = new HashSet<int>();
            for (var y = 50; y < height - 50; y += 4)
                for (var x = 30; x < width - 30; x += 4)
                    colors.Add(bitmap.GetPixel(x, y).ToArgb());
            Assert.True(colors.Count >= 8, "DrawToBitmap produced a blank control surface");
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.FillRectangle(Brushes.Yellow, 0, 0, width, 24);
                graphics.DrawString($"SYNTHETIC TEST FIXTURE - {width}x{height} - NO REAL RECORDS / NO SEND",
                    form.Font, Brushes.Black, 8, 5);
            }
            var directory = VisualEvidenceDirectory();
            Directory.CreateDirectory(directory);
            bitmap.Save(Path.Combine(directory, $"SYNTHETIC_PROTOCOL_{width}x{height}.png"), ImageFormat.Png);
        });
    }

    private static string VisualEvidenceDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../../../evidence/R165Y_GUARDIAN_PROTOCOL_TAB/tests"));

    private sealed class VisualFixtureForm(IGuardianProtocolClient client) : FrmMonitorMvp(
        new MonitorOperacionalConsulta(new EmptyRepository()), "SYNTHETIC", protocolClient: client,
        confirmProtocol: _ => throw new Exception("NO_CONFIRMATION"), copyProtocolJson: _ => throw new Exception("NO_CLIPBOARD"))
    {
        protected override bool ShowWithoutActivation => true;
    }

    private static FrmMonitorMvp CreateForm(IGuardianProtocolClient client, Func<GuardianProtocolSnapshot, bool> confirm, Action<string>? copy = null)
        => new(new MonitorOperacionalConsulta(new EmptyRepository()), "SYNTHETIC", protocolClient: client, confirmProtocol: confirm, copyProtocolJson: copy);

    private static void Bind(FrmMonitorMvp form, Guid? id = null, string contract = "SYNTHETIC-ONLY")
    {
        form.BindingContext ??= new BindingContext();
        var grid = (DataGridView)typeof(FrmMonitorMvp).GetField("Integracoes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        _ = grid.Handle;
        form.Mostrar(new(false, false, new[] { new MonitorIntegracao { IntegracaoId = id ?? Id, Documento = contract } }));
        Application.DoEvents();
    }

    private static T Control<T>(Control form, string name) where T : Control => Assert.IsType<T>(Descendants(form).Single(c => c.Name == name));
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, new object[] { EventArgs.Empty });
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        Assert.True(done(), "UI did not settle");
    }

    private sealed class EmptyRepository : IMonitorReadRepository
    {
        public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct) => Task.FromResult(new MonitorSnapshot(false, false, Array.Empty<MonitorIntegracao>()));
        public Task<MonitorDetalhe> DetalharAsync(Guid id, CancellationToken ct) => Task.FromResult(new MonitorDetalhe([], [], []));
    }

    internal static IEnumerable<Control> Descendants(Control parent)
        => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    internal static void OnSta(Action action, Func<string>? phase = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                System.Windows.Forms.Control.CheckForIllegalCrossThreadCalls = true;
                using var loop = new ApplicationContext();
                using var context = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(context);
                context.Post(_ =>
                {
                    try { action(); }
                    catch (Exception ex) { failure = ex; }
                    finally { loop.ExitThread(); }
                }, null);
                Application.Run(loop);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(12)), "STA test did not finish: " + phase?.Invoke());
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
