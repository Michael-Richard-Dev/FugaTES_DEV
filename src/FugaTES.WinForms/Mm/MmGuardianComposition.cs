using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Persistencia.Mm;
using FugaTES.WinForms.Transportadoras;
namespace FugaTES.WinForms.Mm;
public sealed record MmGuardianOptions(bool Enabled=false,string UrlParaRetorno="",string ActivationReference="",string InitialReconciliationReference="")
{
    public void Validar()
    {
        if(!Enabled)return;
        if(string.IsNullOrWhiteSpace(ActivationReference)||string.IsNullOrWhiteSpace(InitialReconciliationReference))throw new InvalidOperationException("MM_OPERATIONAL_APPROVAL_REQUIRED");
        if(!Uri.TryCreate(UrlParaRetorno,UriKind.Absolute,out var callback)||callback.Scheme!="https"||callback.UserInfo.Length!=0||callback.Fragment.Length!=0)throw new InvalidOperationException("MM_CALLBACK_INVALIDO");
    }
}
public sealed class MmGuardianComposition:IDisposable,IMmGuardianDispatcher
{
    private readonly ConexaoMmPostgreSql db;private readonly HttpClient http;private readonly RegistroProtocolosMmPostgreSql registry;private readonly EnvioAutomaticoMm sender;
    private MmGuardianComposition(ConexaoMmPostgreSql db,HttpClient http,RegistroProtocolosMmPostgreSql registry,EnvioAutomaticoMm sender)
    {this.db=db;this.http=http;this.registry=registry;this.sender=sender;}
    public static async Task<MmGuardianComposition?> CreateAsync(string directory,MmSnapshotStore store,CancellationToken ct)
    {
        HttpClient? http=null;ConexaoMmPostgreSql? db=null;
        try
        {
            var path=Path.Combine(directory,"FugaTES.mm-guardian.json");
            if(!File.Exists(path)){store.Update(x=>x with{GuardianState="MM_GUARDIAN_DESATIVADO"});return null;}
            var options=JsonSerializer.Deserialize<MmGuardianOptions>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidOperationException();
            options.Validar();store.Update(x=>x with{GuardianCallback=options.UrlParaRetorno,GuardianState=options.Enabled?"MM_GUARDIAN_CONECTANDO":"MM_GUARDIAN_DESATIVADO"});
            if(!options.Enabled)return null;
            var profile=CarrierDesktopProfile.Load(Path.Combine(directory,"FugaTES.carrier.json"));
            if(profile["Guardian:BaseUrl"]?.TrimEnd('/')!="https://guardian.cloudprix.com.br")throw new InvalidOperationException("MM_GUARDIAN_ORIGIN_REJECTED");
            http=new HttpClient(new MmGuardianHttpGuard(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false})){Timeout=Timeout.InfiniteTimeSpan};
            var guardianOptions=new GuardianHttpOptions{BaseUrl=new Uri("https://guardian.cloudprix.com.br"),Timeout=TimeSpan.FromSeconds(30)};
            var token=new GuardianTokenClient(http,guardianOptions,new CarrierServiceGuardianCredentials(profile));
            db=await ConexaoMmPostgreSql.AbrirAsync(directory,ct);var registry=new RegistroProtocolosMmPostgreSql(db.Fonte);
            store.Update(x=>x with{GuardianState="MM_GUARDIAN_PRONTO"});
            return new(db,http,registry,new EnvioAutomaticoMm(registry,new GuardianProtocoloMmClient(http,token.ObterTokenAsync,TimeSpan.FromSeconds(30)),options.UrlParaRetorno));
        }
        catch{http?.Dispose();db?.Dispose();store.Update(x=>x with{GuardianState="MM_GUARDIAN_BLOQUEADO_CONFIG"});return null;}
    }
    public async Task<MmGuardianReadback> ProcessarAsync(IReadOnlyList<PedidoCompraMmLeitura> rows,CancellationToken ct,Func<bool> admission)
    {
        var result=await sender.ProcessarAsync(rows,ct,admission);
        return new(result.Bloqueados>0?"MM_GUARDIAN_COM_BLOQUEIOS":"MM_GUARDIAN_VERIFICADO",await registry.LerAsync(ct));
    }
    public void Dispose(){http.Dispose();db.Dispose();}
}
public sealed class MmGuardianHttpGuard(HttpMessageHandler inner):DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var uri=request.RequestUri;
        if(request.Method!=HttpMethod.Post||uri is null||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.Query.Length!=0
           ||uri.GetLeftPart(UriPartial.Authority)!="https://guardian.cloudprix.com.br"
           ||uri.AbsolutePath is not("/api/v1/Guardian/ProtocoloPreCadastro" or "/api/v2/CreateToken"))throw new InvalidOperationException("MM_GUARDIAN_REQUEST_REJECTED");
        return base.SendAsync(request,ct);
    }
}
