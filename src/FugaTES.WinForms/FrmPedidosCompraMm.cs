using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.WinForms.Mm;

namespace FugaTES.WinForms;

/// <summary>Observa o motor selecionado; a interface nunca despacha operações Guardian.</summary>
public partial class FrmPedidosCompraMm : Form
{
    private readonly Func<MmPurchaseOrderSnapshot> read;
    private readonly Action ensureStarted;
    private readonly Func<CancellationToken,Task<MmRemoteReadback>>? remoteRead;
    private readonly CancellationTokenSource readingLifetime=new();
    private bool loadingRemote;
    private MmRemoteReadback? remoteView;
    private readonly System.Windows.Forms.Timer refresh;
    private MmPurchaseOrderSnapshot? current;
    private DateTimeOffset? displayedSuccess;
    private long displayedGuardianRevision=-1;
    public FrmPedidosCompraMm(Func<MmPurchaseOrderSnapshot>? read=null,Action? ensureStarted=null)
    {
        MmSnapshotStore? store=null;
        this.read=read??(()=>MmPurchaseOrderComposition.IsVmObserver(AppContext.BaseDirectory)?throw new InvalidOperationException("MM_REMOTE_BINDING_REQUIRED"):(store??=MmPurchaseOrderComposition.CreateStore(AppContext.BaseDirectory)).Read());
        this.ensureStarted=ensureStarted??MmPurchaseOrderComposition.EnsureStarted;
        InitializeComponent();MontarColunas();ConfigureRetryUi();
        var history=new Button{Name="historicoGuardianMm",Text="Histórico Guardian",Width=165,Height=32};
        history.Click+=(_,_)=>{if(current is null)return;using var dialog=CriarDialogoHistorico(current.GuardianRecords);dialog.ShowDialog(this);};
        atualizar.Parent!.Controls.Add(history);
        refresh=new System.Windows.Forms.Timer(components!){Interval=2000};
        refresh.Tick+=(_,_)=>AtualizarLeitura();
        atualizar.Click+=(_,_)=>AtualizarLeitura();
        filtro.TextChanged+=(_,_)=>ExibirItens();
        centros.SelectedIndexChanged+=(_,_)=>ExibirItens();
        grid.CellContentClick+=(_,e)=>
        {
            if(e.RowIndex<0||e.ColumnIndex<0||grid.Rows[e.RowIndex].DataBoundItem is not MmGuardianMonitorRow item)return;
            if(grid.Columns[e.ColumnIndex].Name=="jsonMm"){using var dialog=CriarDialogoJson(item.Source);dialog.ShowDialog(this);}
            else if(grid.Columns[e.ColumnIndex].Name=="jsonGuardianMm"){using var dialog=CriarDialogoJsonGuardian(item);dialog.ShowDialog(this);}
        };
        Shown+=(_,_)=>
        {
            try{this.ensureStarted();}catch{status.Text="DESCONECTADO — coletor MM não pôde iniciar (MM_START_REQUIRED). Nenhuma consulta da tela foi enviada.";}
            AtualizarLeitura();refresh.Start();
        };
        FormClosed+=(_,_)=>{refresh.Stop();readingLifetime.Cancel();};
    }
    public FrmPedidosCompraMm(Func<CancellationToken,Task<MmRemoteReadback>> remoteRead):this(()=>throw new InvalidOperationException("MM_LOCAL_FALLBACK_FORBIDDEN"),()=>{})
    {this.remoteRead=remoteRead;refresh.Interval=15000;Text="Pedidos de compra MM — motor na VM — PostgreSQL5433";}
    public void AtualizarLeitura()
    {
        if(remoteRead is not null){_=AtualizarRemotaAsync();return;}
        try{ApplySnapshot(read());}catch{ShowReadFailure();}
    }
    public async Task AtualizarRemotaAsync()
    {
        if(remoteRead is null||loadingRemote||IsDisposed||readingLifetime.IsCancellationRequested)return;
        loadingRemote=true;
        try
        {
            var view=await remoteRead(readingLifetime.Token);
            if(IsDisposed||readingLifetime.IsCancellationRequested)return;
            if(view.Provenance!="VM_SNAPSHOT_NOT_JOURNAL"||view.Snapshot.RuntimeMode!="VM"||view.Snapshot.Host!="vm-fugatesbio")throw new InvalidOperationException("MM_VM_SCOPE_REJECTED");
            remoteView=view;ApplySnapshot(view.Snapshot);await RefreshRetryStatusAsync();
        }
        catch(OperationCanceledException) when(readingLifetime.IsCancellationRequested){}
        catch(FugaTES.WinForms.Monitor.PhysicalConnectionException error) when(error.ResourceOnly)
        {if(!IsDisposed)ShowReadFailure(error.Code);}
        catch{if(!IsDisposed)ShowReadFailure();}
        finally{loadingRemote=false;}
    }
    private void ShowReadFailure(string? code=null)
    {
        status.ForeColor=Color.DarkRed;
        if(code is not null)
        {
            status.Text="MM — LEITURA DESATUALIZADA. "+new FugaTES.WinForms.Monitor.PhysicalReadConnection().Failure(code).Message+" Últimos dados exibidos não foram atualizados.";
            return;
        }
        status.Text="DESCONECTADO — LEITURA DESATUALIZADA. "+(remoteRead is null?"Cache MM indisponível ou identidade inválida.":"API privada MM indisponível; confira o túnel/perfil de leitura. Nenhum motor local será iniciado.")+" Últimos dados exibidos não foram atualizados.";
    }
    private void ApplySnapshot(MmPurchaseOrderSnapshot snapshot)
    {
            current=snapshot;
            var fresh=current.IsFresh(DateTimeOffset.UtcNow)&&(remoteRead is null||remoteView?.Fresh==true);
            var situation=fresh?"CONECTADO":"DESCONECTADO — LEITURA DESATUALIZADA";
            status.ForeColor=fresh?Color.DarkGreen:Color.DarkRed;
            status.Text=$"{situation} — SAP REAL · cliente {current.Client} · empresa 3000 · centros 3011/3008\r\n"
                +$"Último sucesso: {Local(current.LastSuccessUtc)} · última tentativa: {Local(current.LastAttemptUtc)} · próximo ciclo: {Local(current.NextPollUtc)}\r\n"
                +$"{Descrever(current.State)} · HTTP {current.Http?.ToString()??"não consultado"} · {current.Pages} página(s) · ciclo {current.Cycles}.\r\nEnvio automático Guardian: {current.GuardianState}.";
            if(displayedSuccess!=current.LastSuccessUtc||displayedGuardianRevision!=current.GuardianRevision||grid.DataSource is null){displayedSuccess=current.LastSuccessUtc;displayedGuardianRevision=current.GuardianRevision;ExibirItens();}
            rodape.Text=$"{current.Items.Select(x=>x.Pedido).Distinct().Count()} pedido(s), {current.Items.Count} item(ns). Último ciclo completo: {current.NewItems} novo(s), {current.ChangedItems} alterado(s). "
                +(remoteRead is null?"Coletor Windows · intervalo de 3 minutos.":"Motor na VM vm-fugatesbio · intervalo de 3 minutos · pedidos criados desde 01/11/2026. Última leitura da API: "+Local(remoteView?.ReadAtUtc)+". ")+" Guardian: "+current.GuardianState+". Observação do serviço; resultados copiados do registro durável MM, não é uma nova consulta SAP/Guardian da tela.";
    }
    private void ExibirItens()
    {
        if(current is null)return;
        var selected=(grid.CurrentRow?.DataBoundItem as MmGuardianMonitorRow)?.Source;
        var term=filtro.Text.Trim();var plant=centros.SelectedItem?.ToString()??"Todos";
        var rows=current.Items.Where(x=>(plant=="Todos"||x.Centro==plant)&&(term.Length==0||x.Pedido.Contains(term,StringComparison.OrdinalIgnoreCase)||x.Material.Contains(term,StringComparison.OrdinalIgnoreCase)||x.Fornecedor.Contains(term,StringComparison.OrdinalIgnoreCase))).ToList();
        var registros=current.GuardianRecords.ToDictionary(x=>x.Chave,StringComparer.Ordinal);
        grid.DataSource=rows.Select(x=>new MmGuardianMonitorRow(x,registros.GetValueOrDefault(FugaTES.Aplicacao.Servicos.Mm.MapeadorProtocoloMm.ChaveNatural(x)),current.GuardianCallback,current.GuardianState)).ToList();
        if(selected is not null)
            foreach(DataGridViewRow row in grid.Rows)
                if(row.DataBoundItem is MmGuardianMonitorRow item&&item.Pedido==selected.Pedido&&item.Item==selected.Item){row.Selected=true;grid.CurrentCell=row.Cells[0];break;}
        quantidade.Text=$"{rows.Count} item(ns) exibido(s)";
    }
    private void MontarColunas()
    {
        grid.Columns.Add(new DataGridViewButtonColumn{Name="jsonMm",HeaderText="JSON SAP",Text="JSON",UseColumnTextForButtonValue=true,Width=70});
        grid.Columns.Add(new DataGridViewButtonColumn{Name="jsonGuardianMm",HeaderText="JSON Guardian",Text="JSON",UseColumnTextForButtonValue=true,Width=110});
        void Column(string name,string title,string property,int width)=>grid.Columns.Add(new DataGridViewTextBoxColumn{Name=name,HeaderText=title,DataPropertyName=property,Width=width,ReadOnly=true});
        Column("pedidoMm","Pedido",nameof(PedidoCompraMmLeitura.Pedido),110);Column("itemMm","Item",nameof(PedidoCompraMmLeitura.Item),65);
        Column("centroMm","Centro",nameof(PedidoCompraMmLeitura.Centro),65);Column("materialMm","Material",nameof(PedidoCompraMmLeitura.Material),125);
        Column("grupoMm","Grupo material",nameof(PedidoCompraMmLeitura.GrupoMaterial),105);Column("quantidadeMm","Quantidade",nameof(PedidoCompraMmLeitura.Quantidade),115);
        grid.Columns["quantidadeMm"]!.DefaultCellStyle.Format="0.###";
        Column("unidadeMm","Unidade",nameof(PedidoCompraMmLeitura.Unidade),70);Column("fornecedorMm","Fornecedor",nameof(PedidoCompraMmLeitura.Fornecedor),110);
        Column("tipoMm","Tipo pedido",nameof(PedidoCompraMmLeitura.TipoPedido),90);Column("estadoMm","Liberação SAP",nameof(PedidoCompraMmLeitura.EstadoSap),100);
        Column("empresaMm","Empresa",nameof(PedidoCompraMmLeitura.Empresa),70);
        Column("guardianEstadoMm","Estado Guardian",nameof(MmGuardianMonitorRow.EstadoGuardian),220);
        Column("guardianHttpMm","HTTP Guardian",nameof(MmGuardianMonitorRow.HttpGuardian),105);
        Column("guardianProtocoloMm","Protocolo Guardian",nameof(MmGuardianMonitorRow.ProtocoloGuardian),140);
        Column("guardianAtualizadoMm","Última tentativa (UTC)",nameof(MmGuardianMonitorRow.UltimaTentativaGuardian),180);
        Column("guardianMensagemMm","Resultado Guardian",nameof(MmGuardianMonitorRow.MensagemGuardian),300);
    }
    private static string Local(DateTimeOffset? timestamp)=>timestamp?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz")??"ainda não disponível";
    private static string Descrever(string state)=>state switch
    {
        "MM_SAP_COMPLETE"=>"Consulta completa; liberação SAP concluída (05)","MM_COLLECTING"=>"Consulta SAP em andamento",
        "MM_NOT_STARTED"=>"Aguardando o coletor", "MM_SAP_AUTHENTICATION_REQUIRED"=>"Credencial SAP não autenticada",
        "MM_SAP_AUTHORIZATION_REQUIRED"=>"Usuário SAP sem autorização para consulta MM", "MM_SAP_QUERY_REJECTED"=>"SAP rejeitou o contrato da consulta",
        "MM_SAP_TIMEOUT"=>"Tempo limite na consulta; última leitura completa preservada", "MM_SAP_RESPONSE_INVALID"=>"Resposta ou paginação SAP inválida; resultado parcial descartado",
        "MM_VM_STOPPED"=>"Motor VM parado; nenhuma nova admissão",_=>"Motor indisponível; conferir a leitura e o estado do serviço MM"
    };
    public static Form CriarDialogoHistorico(IReadOnlyList<RegistroProtocoloMm> records)
    {
        var form=new Form{Text="Histórico Guardian MM — registros preservados — somente leitura",Size=new Size(1100,650),StartPosition=FormStartPosition.CenterParent};
        var table=new DataGridView{Dock=DockStyle.Fill,AutoGenerateColumns=false,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false};
        table.Columns.Add(new DataGridViewButtonColumn{Name="jsonHistoricoMm",HeaderText="JSON Guardian",Text="JSON",UseColumnTextForButtonValue=true,Width=110});
        foreach(var column in new[]{("Pedido","Pedido",110),("Item","Item",65),("Centro","Centro",65),("Estado","Estado",150),("Http","HTTP",65),("ProtocoloId","Protocolo",110),("AtualizadoUtc","Atualizado (UTC)",180),("Mensagem","Resultado",300)})
            table.Columns.Add(new DataGridViewTextBoxColumn{HeaderText=column.Item2,DataPropertyName=column.Item1,Width=column.Item3});
        table.DataSource=records.OrderByDescending(x=>x.AtualizadoUtc).ToList();
        table.CellContentClick+=(_,e)=>
        {
            if(e.RowIndex<0||e.ColumnIndex<0||table.Columns[e.ColumnIndex].Name!="jsonHistoricoMm"||table.Rows[e.RowIndex].DataBoundItem is not RegistroProtocoloMm row)return;
            using var json=new Form{Text=$"JSON registrado Guardian — pedido {row.Pedido} / item {row.Item}",Size=new Size(930,700),StartPosition=FormStartPosition.CenterParent};
            json.Controls.Add(new TextBox{Multiline=true,ReadOnly=true,WordWrap=false,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Font=new Font("Consolas",10),Text=row.JsonEnviado});
            json.Controls.Add(new Label{Dock=DockStyle.Top,Height=60,Text=$"REGISTRO DURÁVEL MM · {row.Estado} · HTTP {row.Http} · SHA-256 {row.Sha256}. Reserva não comprova POST; este documento não é JSON SAP."});
            json.ShowDialog(form);
        };
        form.Controls.Add(table);
        form.Controls.Add(new Label{Dock=DockStyle.Top,Height=48,Text=$"{records.Count} registro(s) preservado(s), inclusive pedidos anteriores ao corte. Cópia da última leitura do registro PostgreSQL pelo motor; nenhuma ação de envio disponível."});
        return form;
    }
    public static Form CriarDialogoJsonGuardian(MmGuardianMonitorRow item)
    {
        var form=new Form{Text=$"JSON Guardian — pedido {item.Pedido} / item {item.Item}",Size=new Size(930,700),StartPosition=FormStartPosition.CenterParent};
        var label=new Label{Dock=DockStyle.Top,Height=58,Text=item.Registro is null?"PRÉVIA — não é prova de envio. Requer configuração MM válida.":$"REGISTRO DURÁVEL MM — estado {item.Registro.Estado}; HTTP {item.HttpGuardian}; SHA-256 {item.Registro.Sha256}. Reserva não prova POST concluído."};
        var text=new TextBox{Name="jsonGuardianMmDocumento",Multiline=true,ReadOnly=true,WordWrap=false,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Font=new Font("Consolas",10),Text=item.JsonGuardian??"JSON indisponível: configure uma URL de retorno MM válida e confira os dados SAP."};
        form.Controls.Add(text);form.Controls.Add(label);return form;
    }
    public static Form CriarDialogoJson(PedidoCompraMmLeitura item)
    {
        var form=new Form{Text=$"JSON SAP consultado — pedido {item.Pedido} / item {item.Item}",Size=new Size(930,700),MinimumSize=new Size(600,450),StartPosition=FormStartPosition.CenterParent};
        var text=new TextBox{Name="jsonMmDocumento",Multiline=true,ReadOnly=true,WordWrap=false,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Font=new Font("Consolas",10),Text=item.Json};
        var label=new Label{Text="SAP REAL · projeção dos campos de cabeçalho e item consultados. Somente leitura; não é payload de criação.",Dock=DockStyle.Top,Height=42,Padding=new Padding(8)};
        form.Controls.Add(text);form.Controls.Add(label);return form;
    }
}
