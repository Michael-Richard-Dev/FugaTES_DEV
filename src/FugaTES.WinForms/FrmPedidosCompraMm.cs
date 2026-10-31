using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.WinForms.Mm;

namespace FugaTES.WinForms;

/// <summary>Observa o coletor local; fechar esta tela não cancela o processo de consulta.</summary>
public partial class FrmPedidosCompraMm : Form
{
    private readonly Func<MmPurchaseOrderSnapshot> read;
    private readonly Action ensureStarted;
    private readonly System.Windows.Forms.Timer refresh;
    private MmPurchaseOrderSnapshot? current;
    private DateTimeOffset? displayedSuccess;
    public FrmPedidosCompraMm(Func<MmPurchaseOrderSnapshot>? read=null,Action? ensureStarted=null)
    {
        MmSnapshotStore? store=null;
        this.read=read??(()=>(store??=new MmSnapshotStore(MmSnapshotStore.DefaultDirectory)).Read());
        this.ensureStarted=ensureStarted??MmPurchaseOrderComposition.EnsureStarted;
        InitializeComponent();MontarColunas();
        refresh=new System.Windows.Forms.Timer(components!){Interval=2000};
        refresh.Tick+=(_,_)=>AtualizarLeitura();
        atualizar.Click+=(_,_)=>AtualizarLeitura();
        filtro.TextChanged+=(_,_)=>ExibirItens();
        centros.SelectedIndexChanged+=(_,_)=>ExibirItens();
        grid.CellContentClick+=(_,e)=>
        {
            if(e.RowIndex<0||e.ColumnIndex<0||grid.Columns[e.ColumnIndex].Name!="jsonMm"||grid.Rows[e.RowIndex].DataBoundItem is not PedidoCompraMmLeitura item)return;
            using var dialog=CriarDialogoJson(item);dialog.ShowDialog(this);
        };
        Shown+=(_,_)=>
        {
            try{this.ensureStarted();}catch{status.Text="DESCONECTADO — coletor MM não pôde iniciar (MM_START_REQUIRED). Nenhuma consulta da tela foi enviada.";}
            AtualizarLeitura();refresh.Start();
        };
        FormClosed+=(_,_)=>refresh.Stop();
    }
    public void AtualizarLeitura()
    {
        try
        {
            current=read();
            var fresh=current.IsFresh(DateTimeOffset.UtcNow);
            var situation=fresh?"CONECTADO":"DESCONECTADO — LEITURA DESATUALIZADA";
            status.ForeColor=fresh?Color.DarkGreen:Color.DarkRed;
            status.Text=$"{situation} — SAP REAL · cliente 110 · empresa 3000 · centros 3011/3008\r\n"
                +$"Último sucesso: {Local(current.LastSuccessUtc)} · última tentativa: {Local(current.LastAttemptUtc)} · próximo ciclo: {Local(current.NextPollUtc)}\r\n"
                +$"{Descrever(current.State)} · HTTP {current.Http?.ToString()??"não consultado"} · {current.Pages} página(s) · ciclo {current.Cycles}.";
            if(displayedSuccess!=current.LastSuccessUtc||grid.DataSource is null){displayedSuccess=current.LastSuccessUtc;ExibirItens();}
            rodape.Text=$"{current.Items.Select(x=>x.Pedido).Distinct().Count()} pedido(s), {current.Items.Count} item(ns). Último ciclo completo: {current.NewItems} novo(s), {current.ChangedItems} alterado(s). "
                +"Coletor a cada 30s, independente da janela; estação ligada e usuário TI conectado. Cache local de leitura, sem envio empresarial.";
        }
        catch
        {
            status.ForeColor=Color.DarkRed;
            status.Text="DESCONECTADO — LEITURA DESATUALIZADA. Cache MM indisponível ou identidade inválida (MM_CACHE_UNAVAILABLE). Últimos dados exibidos não foram atualizados.";
        }
    }
    private void ExibirItens()
    {
        if(current is null)return;
        var selected=grid.CurrentRow?.DataBoundItem as PedidoCompraMmLeitura;
        var term=filtro.Text.Trim();var plant=centros.SelectedItem?.ToString()??"Todos";
        var rows=current.Items.Where(x=>(plant=="Todos"||x.Centro==plant)&&(term.Length==0||x.Pedido.Contains(term,StringComparison.OrdinalIgnoreCase)||x.Material.Contains(term,StringComparison.OrdinalIgnoreCase)||x.Fornecedor.Contains(term,StringComparison.OrdinalIgnoreCase))).ToList();
        grid.DataSource=rows;
        if(selected is not null)
            foreach(DataGridViewRow row in grid.Rows)
                if(row.DataBoundItem is PedidoCompraMmLeitura item&&item.Pedido==selected.Pedido&&item.Item==selected.Item){row.Selected=true;grid.CurrentCell=row.Cells[0];break;}
        quantidade.Text=$"{rows.Count} item(ns) exibido(s)";
    }
    private void MontarColunas()
    {
        grid.Columns.Add(new DataGridViewButtonColumn{Name="jsonMm",HeaderText="JSON SAP",Text="JSON",UseColumnTextForButtonValue=true,Width=70});
        void Column(string name,string title,string property,int width)=>grid.Columns.Add(new DataGridViewTextBoxColumn{Name=name,HeaderText=title,DataPropertyName=property,Width=width,ReadOnly=true});
        Column("pedidoMm","Pedido",nameof(PedidoCompraMmLeitura.Pedido),110);Column("itemMm","Item",nameof(PedidoCompraMmLeitura.Item),65);
        Column("centroMm","Centro",nameof(PedidoCompraMmLeitura.Centro),65);Column("materialMm","Material",nameof(PedidoCompraMmLeitura.Material),125);
        Column("grupoMm","Grupo material",nameof(PedidoCompraMmLeitura.GrupoMaterial),105);Column("quantidadeMm","Quantidade",nameof(PedidoCompraMmLeitura.Quantidade),115);
        grid.Columns["quantidadeMm"]!.DefaultCellStyle.Format="0.###";
        Column("unidadeMm","Unidade",nameof(PedidoCompraMmLeitura.Unidade),70);Column("fornecedorMm","Fornecedor",nameof(PedidoCompraMmLeitura.Fornecedor),110);
        Column("tipoMm","Tipo pedido",nameof(PedidoCompraMmLeitura.TipoPedido),90);Column("estadoMm","Liberação SAP",nameof(PedidoCompraMmLeitura.EstadoSap),100);
        Column("empresaMm","Empresa",nameof(PedidoCompraMmLeitura.Empresa),70);
    }
    private static string Local(DateTimeOffset? timestamp)=>timestamp?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz")??"ainda não disponível";
    private static string Descrever(string state)=>state switch
    {
        "MM_SAP_COMPLETE"=>"Consulta completa; liberação SAP concluída (05)","MM_COLLECTING"=>"Consulta SAP em andamento",
        "MM_NOT_STARTED"=>"Aguardando o coletor", "MM_SAP_AUTHENTICATION_REQUIRED"=>"Credencial SAP não autenticada",
        "MM_SAP_AUTHORIZATION_REQUIRED"=>"Usuário SAP sem autorização para consulta MM", "MM_SAP_QUERY_REJECTED"=>"SAP rejeitou o contrato da consulta",
        "MM_SAP_TIMEOUT"=>"Tempo limite na consulta; última leitura completa preservada", "MM_SAP_RESPONSE_INVALID"=>"Resposta ou paginação SAP inválida; resultado parcial descartado",
        _=>"Coletor indisponível; conferir perfil SAP e tarefa FugaTES-MM-PurchaseOrders"
    };
    public static Form CriarDialogoJson(PedidoCompraMmLeitura item)
    {
        var form=new Form{Text=$"JSON SAP consultado — pedido {item.Pedido} / item {item.Item}",Size=new Size(930,700),MinimumSize=new Size(600,450),StartPosition=FormStartPosition.CenterParent};
        var text=new TextBox{Name="jsonMmDocumento",Multiline=true,ReadOnly=true,WordWrap=false,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,Font=new Font("Consolas",10),Text=item.Json};
        var label=new Label{Text="SAP REAL · projeção dos campos de cabeçalho e item consultados. Somente leitura; não é payload de criação.",Dock=DockStyle.Top,Height=42,Padding=new Padding(8)};
        form.Controls.Add(text);form.Controls.Add(label);return form;
    }
}
