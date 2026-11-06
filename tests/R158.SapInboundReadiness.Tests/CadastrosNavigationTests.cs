using System.Reflection;
using FugaTES.WinForms;
using FugaTES.WinForms.Transportadoras.MasterdataR3;
namespace FugaTES.Testes;
public sealed class CadastrosNavigationTests
{
    private static readonly (string Name,string Label,CategoriaMasterdataR3 Category)[] Expected=
    [ ("emissor","Emissor",CategoriaMasterdataR3.Emissores), ("material","Material",CategoriaMasterdataR3.Materiais),
      ("motorista","Motorista",CategoriaMasterdataR3.Motoristas), ("transportadora","Transportadora",CategoriaMasterdataR3.Transportadoras),
      ("veiculo","Veículo",CategoriaMasterdataR3.Veiculos) ];
    [Fact] public void Integrations_menu_has_cadastros_and_preserves_existing_routes()=>Sta(()=>
    {
        using var menu=new FrmIntegracoes();var buttons=All(menu).OfType<Button>().ToArray();
        Assert.Contains(buttons,b=>b.Name=="cadastros"&&b.Text=="Cadastros"&&b.Enabled);
        foreach(var name in new[]{"ordensVendaSd","pedidosCompraMm","toledoRecebimentos","contratosJson","protocolosPreCadastro"})Assert.Contains(buttons,b=>b.Name==name);
    });
    [Fact] public void Cadastro_buttons_have_the_required_alphabetic_visual_and_keyboard_order()=>Sta(()=>
    {
        using var menu=Create();menu.Show();Application.DoEvents();
        var buttons=All(menu).OfType<Button>().Where(b=>Expected.Any(x=>x.Name==b.Name)).OrderBy(b=>b.Top).ToArray();
        Assert.Equal(Expected.Select(x=>x.Label),buttons.Select(x=>x.Text));Assert.Equal(5,buttons.Length);
        Assert.Equal(Expected.Select(x=>x.Name),buttons.OrderBy(b=>b.TabIndex).Select(b=>b.Name));
        for(int n=1;n<buttons.Length;n++)Assert.True(buttons[n-1].Bottom<=buttons[n].Top);
        menu.Close();
    });
    [Fact] public void Every_button_opens_its_own_integration_category_and_disposes_the_view()=>Sta(()=>
    {
        var opened=new List<CategoriaMasterdataR3>();var views=new List<Form>();
        using var menu=Create(c=>{opened.Add(c);var f=new AutoCloseForm();views.Add(f);return f;});
        menu.Show();Application.DoEvents();
        foreach(var x in Expected)All(menu).OfType<Button>().Single(b=>b.Name==x.Name).PerformClick();
        Assert.Equal(Expected.Select(x=>x.Category),opened);Assert.All(views,f=>Assert.True(f.IsDisposed));menu.Close();
    });
    [Theory]
    [InlineData(CategoriaMasterdataR3.Emissores,"Emissor")]
    [InlineData(CategoriaMasterdataR3.Materiais,"Material")]
    [InlineData(CategoriaMasterdataR3.Motoristas,"Motorista")]
    [InlineData(CategoriaMasterdataR3.Veiculos,"Veículo")]
    public void Category_view_has_only_its_status_without_transportadora_actions(CategoriaMasterdataR3 category,string label)=>Sta(()=>
    {
        var calls=0;var runtime=new OrquestradorCompartilhadoMasterdataR3([new AcaoCategoriaDelegadaMasterdataR3(category,true,true,_=>{calls++;return Task.FromResult(new ResultadoCategoriaMasterdataR3(EstadoMasterdataR3.Sincronizado,3,3,0,0,"OFFLINE_SYNTHETIC",true));})]);
        using var view=(Form)Activator.CreateInstance(typeof(FrmCadastrosGuardianR3),[runtime,null,null,category])!;
        Assert.Contains(label,view.Text);var tabs=Assert.Single(All(view).OfType<TabControl>());
        Assert.Equal(2,tabs.TabPages.Count);Assert.Equal(label,tabs.TabPages[0].Text);
        Assert.DoesNotContain(All(view).OfType<Button>(),b=>b.Text.Contains("CRM010")||b.Text.Contains("Sincronizar agora")||b.Text.Contains("CONSULTA MANUAL"));
        Assert.Contains(All(view).OfType<TextBox>(),t=>t.ReadOnly&&t.Text.Contains("NÃO APURADO"));Assert.Equal(0,calls);
    });
    [Fact] public void Legacy_overview_constructor_and_all_category_tabs_are_preserved()=>Sta(()=>
    {
        using var legacy=new FrmCadastrosGuardianR3();var tabs=Assert.Single(All(legacy).OfType<TabControl>());
        foreach(var c in Enum.GetValues<CategoriaMasterdataR3>())Assert.Contains(tabs.TabPages.Cast<TabPage>(),t=>t.Text==c.ToString());
    });
    [Fact] public void Release_identity_matches_the_version_displayed_by_the_unified_desktop()
    {
        var version=typeof(FrmIntegracoes).Assembly.GetName().Version!;
        Assert.Equal("1.2.18",version.ToString(3));
        Assert.Equal(FugaTES.WinForms.Monitor.UnifiedDesktopSession.Version,version.ToString(3));
    }
    [Fact] public void A_filtered_view_preserves_observed_status_and_does_not_own_the_shared_runtime()=>Sta(()=>
    {
        var calls=0;
        AcaoCategoriaDelegadaMasterdataR3 Runner(CategoriaMasterdataR3 c)=>new(c,true,true,_=>{calls++;return Task.FromResult(new ResultadoCategoriaMasterdataR3(EstadoMasterdataR3.Sincronizado,3,3,0,0,"OFFLINE_SYNTHETIC",true));});
        var runtime=new OrquestradorCompartilhadoMasterdataR3([Runner(CategoriaMasterdataR3.Emissores),Runner(CategoriaMasterdataR3.Materiais)],true);
        runtime.ExecutarAsync(OrigemCicloMasterdataR3.Manual).GetAwaiter().GetResult();
        using(var view=(Form)Activator.CreateInstance(typeof(FrmCadastrosGuardianR3),[runtime,null,null,CategoriaMasterdataR3.Materiais])!)
        {
            view.Show();Application.DoEvents();
            var texts=All(view).OfType<TextBox>().Select(t=>t.Text).ToArray();
            Assert.Contains(texts,t=>t.Contains("Processados: 3")&&t.Contains("Sincronizados: 3"));
            Assert.Contains(texts,t=>t.Contains("Materiais")&&!t.Contains("Emissores"));
            Assert.Equal(2,calls);view.Close();
        }
        Assert.Equal(SituacaoCicloMasterdataR3.Executado,runtime.ExecutarAsync(OrigemCicloMasterdataR3.Manual).GetAwaiter().GetResult().Situacao);
        Assert.Equal(4,calls);
    });
    [Fact] public void Demo_transportadora_can_be_opened_as_an_individual_category_without_reading_SAP()=>Sta(()=>
    {
        var queries=0;using var demo=new FugaTES.WinForms.Transportadoras.SafeDemoReadOnlySessionR50((bp,ct)=>{queries++;throw new InvalidOperationException("NO_EXTERNAL_QUERY");});
        using var view=new FrmCadastrosGuardianR3(null,null,demo,CategoriaMasterdataR3.Transportadoras);
        Assert.Contains("Transportadora",view.Text);var tabs=Assert.Single(All(view).OfType<TabControl>());
        Assert.Equal(2,tabs.TabPages.Count);Assert.Equal("Transportadora",tabs.TabPages[0].Text);Assert.Equal(0,queries);
    });
    private static Form Create(Func<CategoriaMasterdataR3,Form>? factory=null)
    {
        var type=typeof(FrmIntegracoes).Assembly.GetType("FugaTES.WinForms.FrmCadastros");Assert.NotNull(type);
        return (Form)Activator.CreateInstance(type,[factory])!;
    }
    private sealed class AutoCloseForm:Form {protected override void OnShown(EventArgs e){base.OnShown(e);BeginInvoke((Action)Close);}}
    private static IEnumerable<Control> All(Control c){foreach(Control child in c.Controls){yield return child;foreach(var d in All(child))yield return d;}}
    private static void Sta(Action action){Exception? error=null;var t=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});t.SetApartmentState(ApartmentState.STA);t.Start();Assert.True(t.Join(TimeSpan.FromSeconds(20)));if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();}
}
