using FugaTES.Aplicacao.Configuracao;
using System.Diagnostics;
using System.Security.Principal;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.BusinessDocuments;
using FugaTES.WinForms.Runtime;
using FugaTES.WinForms.Transportadoras;
using Microsoft.Extensions.Configuration;

namespace FugaTES.WinForms.Mm;

public static class MmPurchaseOrderComposition
{
    public static MmSapSession CreateSapSession(string directory)
    {
        var config=CarrierDesktopProfile.Load(Path.Combine(directory,"FugaTES.carrier.json"));Validate(config);
        var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false}){Timeout=Timeout.InfiniteTimeSpan};
        return new(http,new PedidosCompraMmConsultaAdapter(http,new CarrierServiceSapAuthentication(config),Ambiente(config)));
    }

    /// <summary>
    /// O ambiente deste desktop, lido do perfil ao lado do executavel. Lanca se o par
    /// origem+sap-client nao for um dos aprovados - e o MESMO par, nao uma origem aprovada com um
    /// cliente aprovado de OUTRO ambiente, que e o que a conferencia separada deixava passar.
    /// </summary>
    public static FugaTES.Aplicacao.Configuracao.AmbienteSap Ambiente(IConfiguration config)
    {
        if(!Uri.TryCreate(config["Sap:BaseUrl"],UriKind.Absolute,out var origin)||origin.Scheme!="https"||origin.UserInfo.Length!=0)
            throw new DesktopStartupException("MM_SAP_ORIGIN_NOT_APPROVED");
        return FugaTES.Aplicacao.Configuracao.AmbientesSap.Por(origin,config["Sap:SapClient"])
            ??throw new DesktopStartupException("MM_SAP_AMBIENTE_NOT_APPROVED");
    }

    public static void Validate(IConfiguration config)
    {
        Ambiente(config);
        if(string.IsNullOrWhiteSpace(config["Carrier:Secrets:SapUser"])||string.IsNullOrWhiteSpace(config["Carrier:Secrets:SapCredentialTarget"]))throw new DesktopStartupException("MM_SAP_PROVIDER_NOT_REFERENCED");
    }
    public static bool IsVmObserver(string directory)
    {
        var file=Path.Combine(directory,"FugaTES.mm-host.json");if(!File.Exists(file))return false;
        try
        {
            using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            var mode=doc.RootElement.GetProperty("Mode").GetString();
            return mode switch{"VmObserver"=>true,"WindowsCollector"=>false,_=>throw new InvalidOperationException()};
        }
        catch{throw new InvalidOperationException("MM_HOST_BINDING_INVALID");}
    }
    private static FugaTES.Aplicacao.Configuracao.AmbienteSap ConfiguredSap()=>Ambiente(CarrierDesktopProfile.Load(Path.Combine(AppContext.BaseDirectory,"FugaTES.carrier.json")));
    public static MmSnapshotStore CreateStore(string directory)
    {
        var sap=Ambiente(CarrierDesktopProfile.Load(Path.Combine(directory,"FugaTES.carrier.json")));
        return new(CacheDirectory(sap),sap);
    }
    private static string CacheDirectory(FugaTES.Aplicacao.Configuracao.AmbienteSap sap)=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FugaTES",sap.Nome,"mm-purchase-orders");
    public static void EnsureStarted()
    {
        if(IsVmObserver(AppContext.BaseDirectory))return;
        // A file lease, not a GUI lifetime/mutex, decides whether a collector is already running.
        var ambiente=ConfiguredSap();
        var store=new MmSnapshotStore(CacheDirectory(ambiente),ambiente);
        using(var probe=store.TryAcquireLease()){if(probe is null)return;}
        var exe=Path.Combine(AppContext.BaseDirectory,"FugaTES.exe");
        if(!File.Exists(exe))throw new InvalidOperationException("MM_OFFICIAL_EXE_REQUIRED");
        using var process=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,ArgumentList={"--mm-purchase-orders"}});
    }
    public static int RunCollector(string[] args)
    {
        if(args.Length!=1||args[0]!="--mm-purchase-orders")return 2;
        if(IsVmObserver(AppContext.BaseDirectory))return 4;
        if(!OperatingSystem.IsWindows()||WindowsIdentity.GetCurrent().IsSystem)return 3;
        MmSnapshotStore? store=null;
        try
        {
            var ambiente=ConfiguredSap();
            store=new(CacheDirectory(ambiente),ambiente);
            using var lease=store.TryAcquireLease();if(lease is null)return 0;
            using var session=CreateSapSession(AppContext.BaseDirectory);
            using var guardian=MmGuardianComposition.CreateAsync(AppContext.BaseDirectory,store,CancellationToken.None).GetAwaiter().GetResult();
            new MmPurchaseOrderCollector(session.Query,store,guardian).RunAsync(FugaTES.Aplicacao.Modelos.Mm.MmCollectionPolicy.Interval,CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch
        {
            // Fail closed without a credentials/exception transcript; Task Scheduler can restart.
            try{store?.Write(store.Read() with {State="MM_COLLECTOR_STARTUP_FAILED",LastAttemptUtc=DateTimeOffset.UtcNow});}catch{}
            return 1;
        }
    }
}

public sealed class MmSapSession(HttpClient http,FugaTES.Aplicacao.Modelos.Mm.IPedidosCompraMmConsulta query):IDisposable
{public FugaTES.Aplicacao.Modelos.Mm.IPedidosCompraMmConsulta Query{get;}=query;public void Dispose()=>http.Dispose();}
