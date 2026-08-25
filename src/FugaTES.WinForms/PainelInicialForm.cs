namespace FugaTES.WinForms;

public partial class PainelInicialForm : Form
{
    private readonly Color corMenuAtivo = Color.FromArgb(34, 102, 141);
    private readonly Color corMenuPadrao = Color.FromArgb(38, 43, 51);

    public PainelInicialForm()
    {
        InitializeComponent();
        SelecionarBotaoMenu(BtnInicio);
    }

    private void BtnInicio_Click(object? sender, EventArgs e)
    {
        SelecionarBotaoMenu(BtnInicio);
        LblTituloConteudo.Text = "Início";
        LblResumoConteudo.Text = "Fundação técnica pronta para receber os incrementos funcionais.";
    }

    private void BtnIntegracoes_Click(object? sender, EventArgs e)
    {
        SelecionarBotaoMenu(BtnIntegracoes);
        LblTituloConteudo.Text = "Integrações";
        LblResumoConteudo.Text = "SAP e Toledo ainda não verificados. Nenhuma chamada externa foi configurada.";
    }

    private void BtnMonitoramento_Click(object? sender, EventArgs e)
    {
        SelecionarBotaoMenu(BtnMonitoramento);
        LblTituloConteudo.Text = "Monitoramento";
        LblResumoConteudo.Text = "Área reservada para auditoria e tentativas de integração futuras.";
    }

    private void BtnConfiguracoes_Click(object? sender, EventArgs e)
    {
        SelecionarBotaoMenu(BtnConfiguracoes);
        LblTituloConteudo.Text = "Configurações";
        LblResumoConteudo.Text = "Configurações reais e segredos permanecem fora de escopo.";
    }

    private void BtnSair_Click(object? sender, EventArgs e)
    {
        Close();
    }

    private void SelecionarBotaoMenu(Button botaoSelecionado)
    {
        foreach (var botao in PnlMenuLateral.Controls.OfType<Button>())
        {
            botao.BackColor = botao == botaoSelecionado ? corMenuAtivo : corMenuPadrao;
        }
    }
}
