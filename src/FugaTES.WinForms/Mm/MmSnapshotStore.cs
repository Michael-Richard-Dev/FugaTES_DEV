using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Integracao.SAP.BusinessDocuments;

namespace FugaTES.WinForms.Mm;

public sealed record MmPurchaseOrderSnapshot
{
    public int Schema {get;init;}=1;
    public string Origin {get;init;}=PedidosCompraMmConsultaAdapter.Origem;
    public string Client {get;init;}="110";
    public string Company {get;init;}="3000";
    public string[] Plants {get;init;}=["3011","3008"];
    public DateTimeOffset? LastAttemptUtc {get;init;}
    public DateTimeOffset? LastSuccessUtc {get;init;}
    public DateTimeOffset? NextPollUtc {get;init;}
    public string State {get;init;}="MM_NOT_STARTED";
    public int? Http {get;init;}
    public int Pages {get;init;}
    public int Cycles {get;init;}
    public int NewItems {get;init;}
    public int ChangedItems {get;init;}
    public IReadOnlyList<PedidoCompraMmLeitura> Items {get;init;}=Array.Empty<PedidoCompraMmLeitura>();
    public bool IsFresh(DateTimeOffset now)=>LastSuccessUtc is {} success&&success<=now.AddMinutes(5)&&now-success<TimeSpan.FromSeconds(90)&&State is "MM_SAP_COMPLETE" or "MM_COLLECTING";
}

/// <summary>Cache de leitura local, não é journal empresarial nem banco PostgreSQL.</summary>
public sealed class MmSnapshotStore
{
    private const int MaxBytes=64*1024*1024;
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private readonly string directory;
    private string SnapshotPath=>Path.Combine(directory,"snapshot.json");
    public static string DefaultDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FugaTES","DEV110","mm-purchase-orders");
    public MmSnapshotStore(string directory)
    {
        if(!Path.IsPathFullyQualified(directory))throw new InvalidOperationException("MM_CACHE_PATH_INVALID");
        this.directory=directory;
        Directory.CreateDirectory(directory);
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("MM_CACHE_LINK_REJECTED");
        if(OperatingSystem.IsWindows())
        {
            var current=WindowsIdentity.GetCurrent().User??throw new InvalidOperationException("MM_WINDOWS_IDENTITY_REQUIRED");
            var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(current);
            foreach(var id in new[]{current,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                acl.AddAccessRule(new FileSystemAccessRule(id,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
        }
    }
    public IDisposable? TryAcquireLease()
    {
        try{return new FileStream(Path.Combine(directory,"collector.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){return null;}
    }
    public MmPurchaseOrderSnapshot Read()
    {
        if(!File.Exists(SnapshotPath))return new();
        var file=new FileInfo(SnapshotPath);
        if(file.LinkTarget is not null||file.Length>MaxBytes)throw new InvalidOperationException("MM_CACHE_INVALID");
        using var stream=new FileStream(SnapshotPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var snapshot=JsonSerializer.Deserialize<MmPurchaseOrderSnapshot>(stream,Json)??throw new InvalidOperationException("MM_CACHE_INVALID");
        Validate(snapshot);return snapshot;
    }
    public void Write(MmPurchaseOrderSnapshot snapshot)
    {
        Validate(snapshot);
        var content=JsonSerializer.SerializeToUtf8Bytes(snapshot,Json);
        if(content.Length>MaxBytes)throw new InvalidOperationException("MM_CACHE_SIZE_LIMIT");
        var temp=Path.Combine(directory,"snapshot."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(content);stream.Flush(flushToDisk:true);}
            File.Move(temp,SnapshotPath,overwrite:true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private static void Validate(MmPurchaseOrderSnapshot value)
    {
        if(value.Schema!=1||value.Origin!=PedidosCompraMmConsultaAdapter.Origem||value.Client!="110"||value.Company!="3000"||value.Plants is null||!value.Plants.SequenceEqual(new[]{"3011","3008"})||value.Items is null||value.Items.Count>100000
            ||value.State is null||value.State.Length>80||!value.State.StartsWith("MM_",StringComparison.Ordinal)||value.State.Any(c=>!char.IsAsciiLetterUpper(c)&&c!='_'))throw new InvalidOperationException("MM_CACHE_IDENTITY_INVALID");
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in value.Items)
            if(row is null||row.Empresa!="3000"||row.Centro is not("3011" or "3008")||row.EstadoSap!="05"||!keys.Add(row.Pedido+"/"+row.Item))throw new InvalidOperationException("MM_CACHE_SCOPE_INVALID");
    }
}
