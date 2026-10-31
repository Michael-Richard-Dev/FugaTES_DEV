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
        return new(http,new PedidosCompraMmConsultaAdapter(http,new CarrierServiceSapAuthentication(config)));
    }
    public static void Validate(IConfiguration config)
    {
        if(!Uri.TryCreate(config["Sap:BaseUrl"],UriKind.Absolute,out var origin)||origin.Scheme!="https"||origin.UserInfo.Length!=0||origin.GetLeftPart(UriPartial.Authority)!=PedidosCompraMmConsultaAdapter.Origem)
            throw new DesktopStartupException("MM_SAP_ORIGIN_NOT_APPROVED");
        if(config["Sap:SapClient"]!="110")throw new DesktopStartupException("MM_SAP_CLIENT_NOT_APPROVED");
        if(string.IsNullOrWhiteSpace(config["Carrier:Secrets:SapUser"])||string.IsNullOrWhiteSpace(config["Carrier:Secrets:SapCredentialTarget"]))throw new DesktopStartupException("MM_SAP_PROVIDER_NOT_REFERENCED");
    }
    public static void EnsureStarted()
    {
        // A file lease, not a GUI lifetime/mutex, decides whether a collector is already running.
        var store=new MmSnapshotStore(MmSnapshotStore.DefaultDirectory);
        using(var probe=store.TryAcquireLease()){if(probe is null)return;}
        var exe=Path.Combine(AppContext.BaseDirectory,"FugaTES.exe");
        if(!File.Exists(exe))throw new InvalidOperationException("MM_OFFICIAL_EXE_REQUIRED");
        using var process=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,ArgumentList={"--mm-purchase-orders"}});
    }
    public static int RunCollector(string[] args)
    {
        if(args.Length!=1||args[0]!="--mm-purchase-orders")return 2;
        if(!OperatingSystem.IsWindows()||WindowsIdentity.GetCurrent().IsSystem)return 3;
        MmSnapshotStore? store=null;
        try
        {
            store=new(MmSnapshotStore.DefaultDirectory);
            using var lease=store.TryAcquireLease();if(lease is null)return 0;
            using var session=CreateSapSession(AppContext.BaseDirectory);
            new MmPurchaseOrderCollector(session.Query,store).RunAsync(TimeSpan.FromSeconds(30),CancellationToken.None).GetAwaiter().GetResult();
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
