using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.WinForms.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;

namespace FugaTES.WinForms;

public partial class FrmMonitorMvp : Form, IMonitorView
{
    private readonly MonitorPresenter presenter;
    private readonly CancellationTokenSource closed = new();
    private readonly IDisposable? lifetime;
    private readonly bool leituraExportada;
    private readonly Func<string>? estadoFonte;
    private readonly GuardianProtocolController protocol;
    private readonly Func<GuardianProtocolSnapshot, bool>? confirmProtocol;
    private readonly Action<string>? copyProtocolJson;
    private bool binding;
    private readonly bool localSimulation;
    private readonly bool physicalSimulated;
    private readonly bool physicalRead;
    private bool ObservedReadOnly=>localSimulation||physicalRead;
    private MonitorDetalhe? selectedDetail;
    private string grupo = "TODOS";
    private Guid? SelectedId => Integracoes.CurrentRow?.DataBoundItem is MonitorIntegracao row ? row.IntegracaoId : null;

    public FrmMonitorMvp() : this(new MonitorOperacionalConsulta(new DesignerRepository()), "Nao informado") { }

    public FrmMonitorMvp(MonitorOperacionalConsulta consulta, string ambiente, IDisposable? lifetime = null,
        Func<Guid, CancellationToken, Task<MonitorAcmDetalhe>>? lerAcm = null,
        bool leituraExportada = false, Func<string>? estadoFonte = null,
        IGuardianProtocolClient? protocolClient = null, Func<GuardianProtocolSnapshot, bool>? confirmProtocol = null,
        Action<string>? copyProtocolJson = null)
    {
        InitializeComponent();
        this.lifetime = lifetime;
        this.leituraExportada = leituraExportada;
        this.estadoFonte = estadoFonte;
        protocol = new(protocolClient);
        localSimulation = protocolClient is LocalSimulationReadback;
        physicalRead=protocolClient is R165AL.Desktop.PhysicalMonitorClient;
        physicalSimulated=protocolClient is R165AL.Desktop.PhysicalMonitorClient physical&&physical.IsPilotOnly;
        if(localSimulation)
        {
            Text="FugaTES R165AJ — SIMULACAO LOCAL — SOMENTE LEITURA";
            RefreshTimer.Interval=500;AutoRefresh.Text="Automatico (0,5 s)";
            Detalhes.SelectedTab=ProtocolTab;
            GenerateProtocol.Text="Ler backend local";
            Layout.RowStyles[4].Height=24;Layout.RowStyles[5].Height=76;
        }
        this.confirmProtocol = confirmProtocol;
        this.copyProtocolJson = copyProtocolJson;
        RenderProtocol();
        if (leituraExportada)
        {
            Text += " - leitura backend exportada";
            TentativasTab.Text = "Tentativas (nao exportadas)";
            AuditoriaTab.Text = "Auditoria (nao exportada)";
            GuardianTab.Text = "Callbacks (nao exportados)";
        }
        Ambiente.Text = "Ambiente: " + MonitorOperacionalConsulta.TextoSeguro(ambiente);
        presenter = new(consulta, this, lerAcm);
        Shown += async (_, _) => { RefreshTimer.Start(); await AtualizarAsync(); };
        FormClosed += (_, _) => { RefreshTimer.Stop(); closed.Cancel(); };
        Atualizar.Click += async (_, _) => await AtualizarAsync();
        RefreshTimer.Tick += async (_, _) => { if (AutoRefresh.Checked) await AtualizarAsync(); };
        Integracoes.SelectionChanged += async (_, _) =>
        {
            if (binding || closed.IsCancellationRequested) return;
            LimparDetalhe();
            MostrarGeral();
            if (SelectedId is Guid id) await presenter.DetalharAsync(id, closed.Token);
            if(ObservedReadOnly){await protocol.GeneratePreviewAsync(closed.Token);RenderProtocol();await PresentLocalObservationAsync();}
        };
        Todos.Click += async (_, _) => { grupo = "TODOS"; await AtualizarAsync(); };
        Pendentes.Click += async (_, _) => { grupo = "PENDENTES"; await AtualizarAsync(); };
        Falhas.Click += async (_, _) => { grupo = "FALHAS"; await AtualizarAsync(); };
        Indeterminados.Click += async (_, _) => { grupo = "INDETERMINADOS"; await AtualizarAsync(); };
        GenerateProtocol.Click += async (_, _) =>
        {
            var operation = protocol.GeneratePreviewAsync(closed.Token);
            RenderProtocol();
            await operation;
            RenderProtocol();
        };
        CopyProtocol.Click += (_, _) => CopyProtocolPayload();
        SendProtocol.Click += async (_, _) => await SendProtocolAsync();
    }

    public Task RefreshReadbackAsync()=>AtualizarAsync();
    private async Task AtualizarAsync()
    {
        if (closed.IsCancellationRequested) return;
        DateTimeOffset? de = De.Checked ? new DateTimeOffset(De.Value.Date) : null;
        DateTimeOffset? ate = Ate.Checked ? new DateTimeOffset(Ate.Value.Date.AddDays(1).AddTicks(-1)) : null;
        await presenter.AtualizarAsync(new(Operacao.Text, Estado.Text, Correlacao.Text, Documento.Text, de, ate, grupo), SelectedId, closed.Token);
        if(ObservedReadOnly){await protocol.GeneratePreviewAsync(closed.Token);RenderProtocol();await PresentLocalObservationAsync();}
    }

    public void Mostrar(MonitorSnapshot snapshot)
    {
        if (closed.IsCancellationRequested || IsDisposed) return;
        var id = SelectedId;
        binding = true;
        try
        {
            Integracoes.DataSource = snapshot.Integracoes.ToList();
            foreach (DataGridViewRow row in Integracoes.Rows)
                if (row.DataBoundItem is MonitorIntegracao item && item.IntegracaoId == id)
                    Integracoes.CurrentCell = row.Cells[0];
            foreach (DataGridViewColumn column in Integracoes.Columns)
            {
                if (leituraExportada && column.Name == "AtualizadoEm")
                {
                    column.Visible = false;
                    continue;
                }
                if (new[] { "Fluxo", "StatusFuncional", "EtapaFuncional", "DecisaoPendente", "SalesOrder", "DeliveryDocument", "PurchaseOrder", "MaterialDocument", "MaterialDocumentYear", "NumeroTicketGuardian" }.Contains(column.Name))
                    column.Visible = snapshot.ProjecaoDisponivel;
                if (column.Name is "CriadoEm" or "AtualizadoEm") column.DefaultCellStyle.Format = "yyyy-MM-dd HH:mm:ss";
                if(physicalSimulated && column.Name=="ProtocoloGuardian")column.HeaderText="Protocolo SIMULADO";
                if (column.Name == "Documento") column.HeaderText = "Contrato / documento";
                if (column.Name == "EstadoMotor") column.HeaderText = "Estado real";
                if (column.Name == "UltimoErro") column.HeaderText = "Erro persistido";
            }
            var ordem = new[] { "CriadoEm", "Operacao", "CorrelationId", "IntegracaoId" };
            for (var index = 0; index < ordem.Length; index++)
                if (Integracoes.Columns[ordem[index]] is { } column) column.DisplayIndex = index;
            Projecao.Text = localSimulation ? "SIMULACAO: exportacao de eventos observados; sem banco fisico" : snapshot.ProjecaoDisponivel ? "Projecao MVP instalada" : "Projeção MVP ainda não instalada";
            Banco.Text = physicalRead?"CONECTADO: PostgreSQL REAL | fugates_dev | porta física 5433":localSimulation ? "H4/JOURNAL EM MEMORIA - NAO E POSTGRESQL" : leituraExportada ? "Backend exportado; sem conexao DB Windows" : "Banco conectado";
            if (estadoFonte is not null) Ambiente.Text = "Ambiente: " + estadoFonte();
            UltimoRefresh.Text = (leituraExportada ? "Leitura local: " : "Ultimo refresh: ") + DateTime.Now.ToString("HH:mm:ss");
            Indicadores.Text = string.Join("    |    ", MonitorOperacionalConsulta.Contadores(snapshot.Integracoes).Select(c => (c.Key == "Concluidos" ? "Concluidos (tecnico)" : c.Key) + ": " + c.Value));
            Escopo.Text = "Integracoes recentes: " + snapshot.Integracoes.Count + " / limite 500 - " + grupo;
            LimparDetalhe(); MostrarGeral();
        }
        finally { binding = false; }
    }

    public void MostrarDetalhe(Guid id, MonitorDetalhe detalhe)
    {
        if (SelectedId != id || closed.IsCancellationRequested || IsDisposed) return;
        Tentativas.DataSource = detalhe.Tentativas.ToList(); Auditoria.DataSource = detalhe.Auditoria.ToList();
        Guardian.DataSource = detalhe.Guardian.ToList();
        selectedDetail = detalhe;
        if (Integracoes.CurrentRow?.DataBoundItem is MonitorIntegracao row)
        {
            FluxoAcm.DataSource = MonitorFluxoAcm.Criar(row, detalhe).ToList();
        }
    }

    public void MostrarAcm(Guid id, MonitorAcmDados dados)
    {
        if (SelectedId != id || selectedDetail is null || closed.IsCancellationRequested || IsDisposed) return;
        if (Integracoes.CurrentRow?.DataBoundItem is not MonitorIntegracao row) return;
        var a = dados.Sanitizar().ComMetadataU(row);
        FluxoAcm.DataSource = MonitorFluxoAcm.Criar(row, selectedDetail, a).ToList();
        IdentidadesAcm.Text = a.CorrelacaoAmbigua ? "Correlacao ambigua - nenhuma identidade inferida" :
            $"Contrato SAP: {a.Contrato ?? "-"} | Versao: {a.Versao?.ToString() ?? "-"} | Centro: {a.Centro ?? "-"} | Recebido: {a.RecebidoEm?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "-"}\r\nGuardian.Codigo confirmado H4: {a.CodigoGuardian ?? "-"} | Sequencial contrato: {a.SequencialContrato ?? "-"} | Ref.: {a.Referencia ?? "-"}\r\nProtocolo: {a.Protocolo ?? "-"} | Sequencial callback: {a.Sequencial ?? "-"} | Callback.ID original: {a.CallbackIdOriginal ?? "-"} | Ticket.Codigo: {a.TicketCodigo ?? "-"}";
        MostrarGeral();
        Geral.AppendText($"\r\nMetadataU: {a.EstadoMetadataU}\r\nHash contrato preparado: {a.HashContratoMetadata ?? "-"}\r\nHash protocolo preparado: {a.HashProtocoloMetadata ?? "-"}");
    }

    public void MostrarFalha()
    {
        Banco.Text = "DESCONECTADO / consulta indisponivel";
        if(physicalRead && estadoFonte is not null)Ambiente.Text=estadoFonte();
        Projecao.Text = "Projecao MVP: nao verificada";
        binding = true; Integracoes.DataSource = null; binding = false;
        Indicadores.Text = "Indicadores indisponiveis"; LimparDetalhe();
    }

    private void MostrarGeral()
    {
        if (Integracoes.CurrentRow?.DataBoundItem is not MonitorIntegracao row) { Geral.Text = ""; return; }
        Geral.Text = $"IntegrationId: {row.IntegracaoId}\r\nCorrelationId: {row.CorrelationId}\r\nDocumento: {row.Documento}\r\nData: {row.CriadoEm:yyyy-MM-dd HH:mm:ss zzz}\r\nOperacao: {row.Operacao}\r\nEstado: {row.EstadoMotor}\r\nTentativa atual: {row.Tentativa}\r\nResultado: {row.UltimoResultado}\r\nErro persistido: {MonitorOperacionalConsulta.TextoSeguro(row.UltimoErro) ?? "-"}\r\nProtocolo Guardian: {row.ProtocoloGuardian ?? "-"}\r\nTicket callback: {row.TicketGuardian ?? "-"}\r\nNumeroTicket resposta: {row.NumeroTicketGuardian ?? "-"}";
    }

    private void LimparDetalhe()
    {
        selectedDetail = null; Geral.Clear(); Tentativas.DataSource = null; Auditoria.DataSource = null;
        Guardian.DataSource = null; FluxoAcm.DataSource = null; IdentidadesAcm.Text = "Detalhe ACM indisponivel / consulta pendente";
        var row = Integracoes.CurrentRow?.DataBoundItem as MonitorIntegracao;
        protocol.Select(row?.IntegracaoId, row?.Documento);
        RenderProtocol();
    }

    private void RenderProtocol()
    {
        if (closed.IsCancellationRequested || IsDisposed) return;
        var preview = protocol.Snapshot;
        GenerateProtocol.Enabled = protocol.CanGenerate;
        CopyProtocol.Enabled = preview?.PayloadJson is not null;
        SendProtocol.Enabled = protocol.CanSend;
        ProtocolLockReason.Text = protocol.CanSend ? "BACKEND_SEND_READY" : protocol.LockReason;
        ProtocolJson.Text = ObservedReadOnly ? preview?.LocalSimulationDetails ?? (physicalRead?"Leitura física indisponível. Atualize para consultar a API privada.":"Aguardando POST local automatico...") : preview?.PayloadJson ?? "";
        ProtocolMetadata.Text = $"Origem: {protocol.PreviewSource}";
        if (preview is null) { ProtocolResult.Clear(); return; }
        static string Safe(string? value) => string.IsNullOrEmpty(value) ? "-" : GuardianProtocolText.Safe(value);
        var host = "-";
        var endpointUrl = "-";
        if (Uri.TryCreate(preview.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "https" or "http")
        {
            host = endpoint.Authority;
            endpointUrl = new UriBuilder(endpoint) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        }
        var byteCount = preview.PayloadJson is null ? "-" : System.Text.Encoding.UTF8.GetByteCount(preview.PayloadJson).ToString();
        if(ObservedReadOnly)
        {
            ProtocolMetadata.AppendText($"\r\nIntegrationId: {preview.IntegrationId} | Contrato: {preview.Contract} | Leitura: {DateTimeOffset.UtcNow:O}\r\n{(physicalRead?"Envio manual bloqueado. PostgreSQL REAL; natureza conforme registro.":"Envio manual bloqueado. H4/core/journal simulados em memoria.")}");
            var simulated=localSimulation||physicalSimulated||preview.AuthenticationContext=="SIMULACAO_CONFIRMADA";
            ProtocolResult.Text=$"{preview.State} | HTTP REST: {preview.HttpStatus?.ToString()??"-"} | {(simulated?"Protocolo SIMULADO persistido":"Protocolo conforme persistência")}: {preview.ProtocolId?.ToString()??"-"}\r\n{preview.SafeBusinessMessage}";
            return;
        }
        ProtocolMetadata.AppendText($"\r\nIntegrationId: {preview.IntegrationId:D} | Contrato: {Safe(preview.Contract)} | Versao: {preview.Version} | Snapshot: {Safe(preview.SnapshotFingerprint)}" +
            $"\r\nMetodo: {Safe(preview.Method)} | URL: {Safe(endpointUrl)} | Host: {Safe(host)}" +
            $"\r\nContent-Type: {Safe(preview.ContentType)} | Accept: {Safe(preview.Accept)} | TransferMode: {Safe(preview.TransferMode)} | AuthenticationContext: {Safe(preview.AuthenticationContext)}" +
            $"\r\nPayload SHA-256: {Safe(preview.PayloadSha256)} | Content-Length (UTF-8): {byteCount}");
        ProtocolResult.Text = $"Estado: {Safe(preview.State)} | HTTP: {preview.HttpStatus?.ToString() ?? "-"} | ProtocolId: {preview.ProtocolId?.ToString() ?? "-"}" +
            $"\r\nMensagem: {Safe(preview.SafeBusinessMessage)}\r\nResposta sanitizada: {Safe(preview.SafeResponseBody)}";
    }

    private void CopyProtocolPayload()
    {
        if (protocol.Snapshot?.PayloadJson is not { } exactJson) return;
        try
        {
            // Copy the backend's exact string, not text normalized by a native edit control.
            if (copyProtocolJson is not null) copyProtocolJson(exactJson);
            else Clipboard.SetText(exactJson);
        }
        catch (System.Runtime.InteropServices.ExternalException) { ProtocolResult.AppendText("\r\nCLIPBOARD_UNAVAILABLE"); }
    }

    private async Task SendProtocolAsync()
    {
        if (!protocol.CanSend || protocol.Snapshot is not { } preview) return;
        var confirmed = confirmProtocol?.Invoke(preview) ?? MessageBox.Show(this,
            $"Enviar protocolo pelo backend?\r\nIntegrationId: {preview.IntegrationId:D}\r\nContrato: {GuardianProtocolText.Safe(preview.Contract)} / {preview.Version}\r\nSHA-256: {preview.PayloadSha256}",
            "Confirmar envio de protocolo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        if (!confirmed || !ReferenceEquals(preview, protocol.Snapshot) || closed.IsCancellationRequested) return;
        var operation = protocol.SendAsync(true, closed.Token);
        RenderProtocol();
        await operation;
        RenderProtocol();
    }

    private sealed class DesignerRepository : IMonitorReadRepository
    {
        public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct) => throw new InvalidOperationException();
        public Task<MonitorDetalhe> DetalharAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException();
    }
}
