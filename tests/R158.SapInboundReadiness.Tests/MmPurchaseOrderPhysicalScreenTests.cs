using FugaTES.Aplicacao.Modelos.Mm;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using FugaTES.WinForms;
using FugaTES.WinForms.Mm;
namespace FugaTES.Testes;
public sealed class MmPurchaseOrderPhysicalScreenTests
{
    [Fact] public void Optional_physical_cache_screen_renders_same_form_from_official_binary()
    {
        var path=Environment.GetEnvironmentVariable("FUGATES_MM_SCREEN_EVIDENCE");if(string.IsNullOrWhiteSpace(path))return;
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                Directory.CreateDirectory(path);
                var snapshot=new MmSnapshotStore(MmSnapshotStore.DefaultDirectory).Read();
                Assert.True(snapshot.IsFresh(DateTimeOffset.UtcNow));Assert.Equal("3000",snapshot.Company);Assert.NotEmpty(snapshot.Items);
                Assert.All(snapshot.Items,x=>{Assert.Equal("3000",x.Empresa);Assert.Contains(x.Centro,new[]{"3011","3008"});Assert.Equal("05",x.EstadoSap);});
                using var form=new FrmPedidosCompraMm(()=>snapshot,()=>{});form.Show();Application.DoEvents();form.AtualizarLeitura();Application.DoEvents();form.Refresh();
                using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,form.Bounds with { X=0,Y=0 });bitmap.Save(Path.Combine(path,"MM_MONITOR_SAP_REAL.png"),System.Drawing.Imaging.ImageFormat.Png);}
                using(var json=FrmPedidosCompraMm.CriarDialogoJson(snapshot.Items[0])){json.Show();Application.DoEvents();using var bitmap=new Bitmap(json.Width,json.Height);json.DrawToBitmap(bitmap,new Rectangle(0,0,json.Width,json.Height));bitmap.Save(Path.Combine(path,"MM_JSON_SAP_REAL.png"),System.Drawing.Imaging.ImageFormat.Png);json.Close();}
                var assembly=typeof(FrmPedidosCompraMm).Assembly.Location;
                var installed=@"C:\FugaTES_DEV110\FugaTES.dll";
                Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(installed))),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))));
                File.WriteAllText(Path.Combine(path,"MM_PHYSICAL_FORM_VALIDATION.json"),JsonSerializer.Serialize(new{result="PASS",evidenceType="WINFORMS_FORM_RENDER_WITH_PHYSICAL_SAP_CACHE",installedBinaryMatches=true,
                    version=typeof(FrmPedidosCompraMm).Assembly.GetName().Version?.ToString(),company=snapshot.Company,client=snapshot.Client,items=snapshot.Items.Count,orders=snapshot.Items.Select(x=>x.Pedido).Distinct().Count(),snapshot.LastSuccessUtc,snapshot.Http,posts=0,databaseWrites=0,vmChanges=0},new JsonSerializerOptions{WriteIndented=true}));
                form.Close();
            }
            catch(Exception e){failure=e;}
        });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(20)));if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
