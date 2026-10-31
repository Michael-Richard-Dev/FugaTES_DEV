using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Forms;
using FugaTES.WinForms;
using FugaTES.WinForms.Toledo;
using FugaTES.Aplicacao.Consultas.RecebimentosToledo;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using R165AL;
using R165AL.Desktop;
namespace FugaTES.Testes;
public sealed class ToledoReceiptsFormTests
{
 static readonly Guid Receipt=Guid.Parse("12345678-1111-2222-3333-444444444444");
 static RecebimentoToledoLinha Row()=>new(Receipt,"TOLEDO","3011","0020318",new DateTime(2026,10,27,12,0,22),DateTimeOffset.UtcNow,"RECEIVED_PENDING_CORRELATION","PRIVATE_NGINX_ALLOWLIST_DEV","0000001155",1,"H4_NOT_FOUND");
 const string Original="{\"SistemaBalanca\":\"Toledo\",\"IdPesagem\":\"0020318\",\"CampoAdicionalNaoReconstruido\":\"PRESERVAR\",\"to_Pesagem\":[{\"ContratoACM\":\"0000001155\"}]}";
 [Fact] public void Integrations_contains_a_read_only_Toledo_receipts_entry_and_preserves_other_menus()
 {
  GuardianProtocolFormTests.OnSta(()=>{using var form=new FrmIntegracoes();var controls=GuardianProtocolFormTests.Descendants(form).ToArray();
   Assert.Contains(controls,c=>c.Name=="toledoRecebimentos"&&c.Text.Contains("Toledo")&&!c.Enabled);
   Assert.Contains(controls,c=>c.Name=="contratosJson");Assert.Contains(controls,c=>c.Name=="protocolosPreCadastro");});
 }
 [Fact] public void Menu_is_enabled_only_when_the_existing_reader_is_injected()
 {
  GuardianProtocolFormTests.OnSta(()=>{using var form=new FrmIntegracoes(toledoRead:()=>new(new Fake()));
   Assert.True(GuardianProtocolFormTests.Descendants(form).Single(c=>c.Name=="toledoRecebimentos").Enabled);});
 }
 [Fact] public void Grid_is_readonly_and_JSON_is_loaded_only_after_explicit_action()
 {
  GuardianProtocolFormTests.OnSta(()=>{var repo=new Fake();using var form=new FrmRecebimentosToledo(repo);Finish(form.ConsultarAsync());
   var grid=GuardianProtocolFormTests.Descendants(form).OfType<DataGridView>().Single();
   Assert.Equal(1,grid.Rows.Count);Assert.True(grid.ReadOnly);Assert.False(grid.AllowUserToAddRows);Assert.True(grid.Columns.Contains("json"));
   Assert.Contains(grid.Columns.Cast<DataGridViewColumn>(),c=>c.Name=="reenviarSap");Assert.True(grid.Columns.Contains("EstadoSap"));Assert.Equal(0,repo.JsonReads);
   using var json=Finish(form.PrepararJsonAsync(0));Assert.NotNull(json);Assert.Equal(1,repo.JsonReads);Assert.Equal(Receipt,json.ReciboId);
   using var doc=JsonDocument.Parse(json.JsonExibido);Assert.Equal("PRESERVAR",doc.RootElement.GetProperty("CampoAdicionalNaoReconstruido").GetString());
   Assert.Equal("0020318",doc.RootElement.GetProperty("IdPesagem").GetString());
   Assert.True(GuardianProtocolFormTests.Descendants(json).OfType<TextBox>().Single().ReadOnly);});
 }
 [Fact] public void A_foreign_receipt_or_missing_original_is_not_displayed_as_the_requested_JSON()
 {
  GuardianProtocolFormTests.OnSta(()=>{var repo=new Fake{Foreign=true};using var form=new FrmRecebimentosToledo(repo);Finish(form.ConsultarAsync());
   Assert.Null(Finish(form.PrepararJsonAsync(0)));Assert.Contains("Não foi possível",Status(form));
   repo.Foreign=false;repo.FailJson=true;Assert.Null(Finish(form.PrepararJsonAsync(0)));});
 }
 [Fact] public void Read_failure_does_not_become_an_empty_success_or_demo_and_dispose_is_idempotent()
 {
  GuardianProtocolFormTests.OnSta(()=>{var repo=new Fake{FailList=true};var form=new FrmRecebimentosToledo(repo);Finish(form.ConsultarAsync());
   Assert.Empty(form.Recebimentos);Assert.Contains("indisponível",Status(form));form.Dispose();form.Dispose();});
 }
 [Theory][InlineData("0000001155")][InlineData("0020318")][InlineData("12345678-1111-2222-3333-444444444444")]
 public void Filter_preserves_identifiers_and_never_normalizes_leading_zeroes(string value)=>Assert.Equal(value,RecebimentosToledoFiltro.Normalizar(" "+value+" "));
 [Theory][InlineData("1 OR 1=1")][InlineData("%")][InlineData("abc")][InlineData("00000000-0000-0000-0000-000000000000")]
 public void Invalid_filter_is_rejected(string value)=>Assert.Throws<ArgumentException>(()=>RecebimentosToledoFiltro.Normalizar(value));
 [Fact] public async Task Client_uses_only_authenticated_GET_for_list_and_selected_original()
 {
  var wire=new Wire();using var client=new PhysicalMonitorClient(new("http://127.0.0.1:18761"),new string('A',64),PilotPlan.Create(),DevReadScope.PilotId,wire,unified:true);
  var list=await client.ConsultarRecebimentosAsync("0000001155",default);Assert.Equal(Receipt,Assert.Single(list.Rows).ReciboId);
  var json=await client.ObterJsonRecebidoAsync(Receipt,default);Assert.Equal(Original,json.DocumentoJson);
  Assert.Equal(new[]{"/dev/read/toledo","/dev/read/toledo-json"},wire.Paths);Assert.True(client.Connection.Connected);
 }
 [Fact] public async Task Wrong_physical_database_identity_is_rejected_before_returning_receipts()
 {
  using var client=new PhysicalMonitorClient(new("http://127.0.0.1:18761"),new string('A',64),PilotPlan.Create(),DevReadScope.PilotId,new Wire{WrongPort=true},unified:true);
  await Assert.ThrowsAsync<InvalidOperationException>(()=>client.ConsultarRecebimentosAsync(null,default));Assert.False(client.Connection.Connected);
 }
 [Fact] public void Optional_physical_installed_profile_read_and_JSON_button_validation()
 {
  var folder=Environment.GetEnvironmentVariable("FUGATES_TOLEDO_PHYSICAL_UI_EVIDENCE");
  if(string.IsNullOrWhiteSpace(folder))return; // Explicit physical acceptance only, never default database access.
  Exception? failure=null;var thread=new Thread(()=>
  {
   try
   {
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Control.CheckForIllegalCrossThreadCalls=true;
    using var loop=new ApplicationContext();using var context=new WindowsFormsSynchronizationContext();SynchronizationContext.SetSynchronizationContext(context);
    context.Post(_=>
    {
     try
     {
      var install=@"C:\FugaTES_DEV110";var installed=Path.Combine(install,"FugaTES.dll");
      var assemblyContext=new InstalledDesktopContext(install);
      var assembly=assemblyContext.LoadFromAssemblyPath(installed);
      Assert.Equal(Path.GetFullPath(installed),Path.GetFullPath(assembly.Location));
      using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(folder)!,"DESKTOP_INSTALL_RESULT.json")));
      var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)));
      Assert.Equal(manifest.RootElement.GetProperty("assembly_sha256").GetString(),hash);
      var sessionType=assembly.GetType("FugaTES.WinForms.Monitor.UnifiedDesktopSession",true)!;
      using var session=(IDisposable)sessionType.GetMethod("Load")!.Invoke(null,[install])!;
      using var form=(Form)sessionType.GetMethod("CreateToledoReceipts")!.Invoke(session,null)!;
      form.Show();var deadline=DateTime.UtcNow.AddSeconds(35);
      var refresh=(Button)GuardianProtocolFormTests.Descendants(form).Single(c=>c.Name=="ToledoReceiptRefresh");
      object[] Rows()=>((System.Collections.IEnumerable)form.GetType().GetProperty("Recebimentos")!.GetValue(form)!).Cast<object>().ToArray();
      while((!refresh.Enabled||Rows().Length==0)&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(20);}
      var rows=Rows();Assert.NotEmpty(rows);var connection=sessionType.GetProperty("Connection")!.GetValue(session)!;
      object? Prop(object o,string name)=>o.GetType().GetProperty(name)!.GetValue(o);
      Assert.True((bool)Prop(connection,"Connected")!);
      var target=Guid.Parse("cda48d10-fa21-48ca-98fa-95b33e98077d");
      var index=Array.FindIndex(rows,x=>(Guid)Prop(x,"ReciboId")! == target);Assert.True(index>=0,"Confirmed physical receipt was not listed.");
      var load=(Task)form.GetType().GetMethod("PrepararJsonAsync")!.Invoke(form,[index])!;deadline=DateTime.UtcNow.AddSeconds(20);
      while(!load.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(20);}
      Assert.True(load.IsCompleted);load.GetAwaiter().GetResult();
      using var dialog=(Form?)load.GetType().GetProperty("Result")!.GetValue(load);Assert.NotNull(dialog);
      dialog.Show(form);Application.DoEvents();Assert.True(dialog.Visible);
      using var original=JsonDocument.Parse((string)Prop(dialog,"JsonExibido")!);Assert.Equal("Toledo",original.RootElement.GetProperty("SistemaBalanca").GetString());
      Assert.Equal(target,(Guid)Prop(dialog,"ReciboId")!);Assert.True(original.RootElement.GetProperty("to_Pesagem").GetArrayLength()>0);
      Assert.True(GuardianProtocolFormTests.Descendants(dialog).OfType<TextBox>().Single().ReadOnly);
      dialog.Close();Application.DoEvents();Directory.CreateDirectory(folder);
      using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(folder,"TOLEDO_REAL_RECEIPTS.png"),System.Drawing.Imaging.ImageFormat.Png);}
      var result=new{result="PASS",version=assembly.GetName().Version!.ToString(),loadedAssemblyPath=assembly.Location,installedAssemblyMatches=true,connection=Prop(connection,"Connected"),cluster=Prop(connection,"Cluster"),port=Prop(connection,"ServerPort"),database=Prop(connection,"Database"),rows=rows.Length,receipt=target,jsonButton=true,jsonOpened=true,jsonValid=true,jsonReadOnly=true,jsonContentExported=false,personalDataExported=false,businessPosts=0};
      File.WriteAllText(Path.Combine(folder,"PHYSICAL_UI_RESULT.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));form.Close();
     }
     catch(Exception ex){failure=ex;}
     finally{loop.ExitThread();}
    },null);Application.Run(loop);
   }
   catch(Exception ex){failure=ex;}
  });thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();
  Assert.True(thread.Join(TimeSpan.FromSeconds(60)),"Physical UI validation timed out.");
  if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
 }
 sealed class InstalledDesktopContext(string folder):System.Runtime.Loader.AssemblyLoadContext(isCollectible:true)
 {
  protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName name)
  {
   if(name.Name?.StartsWith("FugaTES",StringComparison.Ordinal)==true)
   {var file=Path.Combine(folder,name.Name+".dll");if(File.Exists(file))return LoadFromAssemblyPath(file);}
   return null;
  }
 }
 static string Status(Form f)=>GuardianProtocolFormTests.Descendants(f).Single(c=>c.Name=="ToledoReceiptStatus").Text;
 static void Finish(Task t){var until=DateTime.UtcNow.AddSeconds(5);while(!t.IsCompleted&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}Assert.True(t.IsCompleted);t.GetAwaiter().GetResult();}
 static T Finish<T>(Task<T> t){Finish((Task)t);return t.GetAwaiter().GetResult();}
 sealed class Fake:IRecebimentosToledoReadRepository
 {
  public int JsonReads;public bool FailList,Foreign,FailJson;
  public Task<RecebimentosToledoSnapshot> ConsultarRecebimentosAsync(string? busca,CancellationToken ct)=>FailList?Task.FromException<RecebimentosToledoSnapshot>(new IOException("SYNTHETIC")):Task.FromResult(new RecebimentosToledoSnapshot([Row()],false,DateTimeOffset.UtcNow));
  public Task<RecebimentoToledoDocumento> ObterJsonRecebidoAsync(Guid id,CancellationToken ct){JsonReads++;return FailJson?Task.FromException<RecebimentoToledoDocumento>(new IOException("SYNTHETIC")):Task.FromResult(new RecebimentoToledoDocumento(Foreign?Guid.NewGuid():id,Original,new string('A',64)));}
 }
 sealed class Wire:HttpMessageHandler
 {
  public bool WrongPort;public List<string> Paths=[];
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   Assert.Equal(HttpMethod.Get,request.Method);Assert.Single(request.Headers.GetValues("X-R165AL-Read-Key"));var path=request.RequestUri!.AbsolutePath;Paths.Add(path);
   var identity=new DevDatabaseIdentity(WrongPort?"18/main":"18/fugatesacmdev",WrongPort?5432:5433,"fugates_dev","fugates_acm_hml_app",true,DateTimeOffset.UtcNow);
   object value=path.EndsWith("toledo-json")?new DevReadEnvelope<RecebimentoToledoDocumento>(identity,new(Receipt,Original,new string('A',64))):new DevReadEnvelope<RecebimentosToledoSnapshot>(identity,new([Row()],false,DateTimeOffset.UtcNow));
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(value)});
  }
 }
}
