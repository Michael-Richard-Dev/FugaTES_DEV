using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.WinForms;
using FugaTES.WinForms.Mm;
namespace FugaTES.Testes;
public sealed class MmPurchaseOrderScreenTests
{
    [Fact] public void Menu_preserves_sd_and_adds_mm_without_business_dispatch_action()=>Sta(()=>
    {using var menu=new FrmIntegracoes();var buttons=All(menu).OfType<Button>().ToArray();Assert.Contains(buttons,x=>x.Name=="ordensVendaSd");Assert.Contains(buttons,x=>x.Name=="pedidosCompraMm"&&x.Enabled);Assert.Contains(buttons,x=>x.Name=="toledoRecebimentos");});
    [Fact] public void Screen_shows_real_item_fields_company_status_and_json_button()=>Sta(()=>
    {
        using var form=Screen(()=>Fresh());form.Show();Application.DoEvents();form.GetType().GetMethod("AtualizarLeitura")!.Invoke(form,null);
        var grid=Assert.Single(All(form).OfType<DataGridView>());Assert.Equal(1,grid.Rows.Count);
        Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),x=>x.Name=="jsonMm"&&x is DataGridViewButtonColumn);
        Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),x=>x.DataPropertyName=="GrupoMaterial");
        Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),x=>x.DataPropertyName=="Fornecedor");
        Assert.Contains(All(form).OfType<Label>(),x=>x.Text.Contains("CONECTADO")&&x.Text.Contains("3000"));
        Assert.DoesNotContain(All(form).OfType<Button>(),x=>x.Text.Contains("Enviar")||x.Text.Contains("Reenviar"));
    });
    [Fact] public void Failed_read_retains_grid_and_marks_it_stale_instead_of_empty_success()=>Sta(()=>
    {
        var snapshot=Fresh();using var form=Screen(()=>snapshot);form.Show();Application.DoEvents();form.GetType().GetMethod("AtualizarLeitura")!.Invoke(form,null);
        snapshot=snapshot with{State="MM_SAP_AUTHENTICATION_REQUIRED",Http=401};form.GetType().GetMethod("AtualizarLeitura")!.Invoke(form,null);
        Assert.Equal(1,Assert.Single(All(form).OfType<DataGridView>()).Rows.Count);
        Assert.Contains(All(form).OfType<Label>(),x=>x.Text.Contains("DESCONECTADO")&&x.Text.Contains("DESATUALIZADA")&&x.Text.Contains("401"));
    });
    [Fact] public void Json_dialog_displays_the_read_order_and_item_not_an_invented_create_payload()=>Sta(()=>
    {
        var type=Type();using var json=(Form)type.GetMethod("CriarDialogoJson")!.Invoke(null,[Fresh().Items[0]])!;
        var text=Assert.Single(All(json).OfType<TextBox>());Assert.True(text.ReadOnly);Assert.Contains("4455000001",text.Text);Assert.Contains("00010",text.Text);
    });
    [Fact] public void Closing_observer_does_not_dispose_or_cancel_background_collector()=>Sta(()=>
    {var starts=0;using(var form=Screen(()=>Fresh(),()=>starts++)){form.Show();Application.DoEvents();form.Close();}Assert.Equal(1,starts);});
    [Theory][InlineData("https://vhfufds4ci.sap.fugacouros.com.br:44300","110")][InlineData("https://vhfufqs4ci.sap.fugacouros.com.br:44300","120")]
    public void Screen_reports_the_SAP_client_of_the_observed_snapshot(string origin,string client)=>Sta(()=>
    {using var form=Screen(()=>Fresh() with{Origin=origin,Client=client});form.Show();Application.DoEvents();form.GetType().GetMethod("AtualizarLeitura")!.Invoke(form,null);Assert.Contains(All(form).OfType<Label>(),x=>x.Text.Contains("cliente "+client));});
    static MmPurchaseOrderSnapshot Fresh()=>new(){LastSuccessUtc=DateTimeOffset.UtcNow,LastAttemptUtc=DateTimeOffset.UtcNow,NextPollUtc=DateTimeOffset.UtcNow.AddSeconds(30),State="MM_SAP_COMPLETE",Http=200,Pages=2,Items=[new("3000","4455000001","00010","ZCOL","3011","00001000249","L002",50000.123m,"KG","0000200001","05","{\"PurchaseOrder\":\"4455000001\",\"PurchaseOrderItem\":\"00010\"}")]};
    static Type Type(){var type=typeof(FrmIntegracoes).Assembly.GetType("FugaTES.WinForms.FrmPedidosCompraMm");Assert.NotNull(type);return type;}
    static Form Screen(Func<MmPurchaseOrderSnapshot> read,Action? started=null)=>(Form)Activator.CreateInstance(Type(),read,started??(()=>{}))!;
    static IEnumerable<Control> All(Control control){foreach(Control child in control.Controls){yield return child;foreach(var descendant in All(child))yield return descendant;}}
    static void Sta(Action test){Exception? error=null;var thread=new Thread(()=>{try{test();}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(15)));if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();}
}
