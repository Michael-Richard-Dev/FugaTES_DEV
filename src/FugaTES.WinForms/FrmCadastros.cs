using FugaTES.WinForms.Transportadoras.MasterdataR3;
namespace FugaTES.WinForms;
public partial class FrmCadastros : Form
{
    private readonly Func<CategoriaMasterdataR3,Form> abrirCategoria;
    public FrmCadastros(Func<CategoriaMasterdataR3,Form>? abrirCategoria=null)
    {
        InitializeComponent();
        this.abrirCategoria=abrirCategoria??CriarTelaPadrao;
        emissor.Click+=(_,_)=>Abrir(CategoriaMasterdataR3.Emissores);
        material.Click+=(_,_)=>Abrir(CategoriaMasterdataR3.Materiais);
        motorista.Click+=(_,_)=>Abrir(CategoriaMasterdataR3.Motoristas);
        transportadora.Click+=(_,_)=>Abrir(CategoriaMasterdataR3.Transportadoras);
        veiculo.Click+=(_,_)=>Abrir(CategoriaMasterdataR3.Veiculos);
        voltar.Click+=(_,_)=>Close();
    }
    private void Abrir(CategoriaMasterdataR3 categoria)
    {
        using var tela=abrirCategoria(categoria);
        tela.ShowDialog(this);
    }
    private static Form CriarTelaPadrao(CategoriaMasterdataR3 categoria)
        =>categoria==CategoriaMasterdataR3.Emissores
            ?Cadastros.EmissoresSapComposition.Criar()
            :categoria is CategoriaMasterdataR3.Transportadoras or CategoriaMasterdataR3.Materiais
            ?Cadastros.CadastrosSapComposition.Criar(categoria==CategoriaMasterdataR3.Materiais?FugaTES.Aplicacao.Consultas.CadastrosSap.CategoriaCadastroSap.Materiais:FugaTES.Aplicacao.Consultas.CadastrosSap.CategoriaCadastroSap.Transportadoras)
            :new FrmCadastrosGuardianR3(null,null,null,categoria);
    internal static string NomeCategoria(CategoriaMasterdataR3 categoria)=>categoria switch
    {
        CategoriaMasterdataR3.Emissores=>"Emissor",
        CategoriaMasterdataR3.Materiais=>"Material",
        CategoriaMasterdataR3.Motoristas=>"Motorista",
        CategoriaMasterdataR3.Transportadoras=>"Transportadora",
        CategoriaMasterdataR3.Veiculos=>"Veículo",
        _=>throw new ArgumentOutOfRangeException(nameof(categoria))
    };
}
