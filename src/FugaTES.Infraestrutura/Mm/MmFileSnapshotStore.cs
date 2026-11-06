using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Infraestrutura.Mm;
/// <summary>Cache de leitura local, não é journal empresarial nem banco PostgreSQL.</summary>
public class MmFileSnapshotStore : IMmSnapshotStore
{
    private const int MaxBytes=64*1024*1024;
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private readonly string directory;
    private readonly object gate=new();
    private string SnapshotPath=>Path.Combine(directory,"snapshot.json");
    public static string DefaultDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FugaTES","DEV110","mm-purchase-orders");
    public MmFileSnapshotStore(string directory,bool readOnly=false)
    {
        if(!Path.IsPathFullyQualified(directory))throw new InvalidOperationException("MM_CACHE_PATH_INVALID");
        this.directory=directory;
        this.readOnly=readOnly;
        if(!readOnly){if(OperatingSystem.IsLinux())Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);else Directory.CreateDirectory(directory);}
        ValidatePath(directory,true);
    }
    private readonly bool readOnly;
    private static void ValidatePath(string path,bool folder)
    {
        FileSystemInfo current=folder?new DirectoryInfo(path):new FileInfo(path);
        if(!current.Exists||current.LinkTarget is not null||(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("MM_CACHE_LINK_OR_MISSING");
        for(var ancestor=folder?((DirectoryInfo)current).Parent:((FileInfo)current).Directory;ancestor is not null;ancestor=ancestor.Parent)
            if(ancestor.LinkTarget is not null)throw new InvalidOperationException("MM_CACHE_LINK_REJECTED");
        if(OperatingSystem.IsLinux())
        {
            var mode=File.GetUnixFileMode(path);
            if((mode&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)
                throw new InvalidOperationException("MM_CACHE_PERMISSIONS_INVALID");
        }
    }
    public IDisposable? TryAcquireLease()
    {
        if(readOnly)throw new InvalidOperationException("MM_CACHE_READ_ONLY");
        try{return new FileStream(Path.Combine(directory,"collector.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){return null;}
    }
    public MmPurchaseOrderSnapshot Read(){lock(gate)return ReadCore();}
    public void Write(MmPurchaseOrderSnapshot value){lock(gate)WriteCore(value);}
    public void Update(Func<MmPurchaseOrderSnapshot,MmPurchaseOrderSnapshot> update){lock(gate)WriteCore(update(ReadCore()));}
    private MmPurchaseOrderSnapshot ReadCore()
    {
        if(!File.Exists(SnapshotPath))return new();
        ValidatePath(SnapshotPath,false);
        var file=new FileInfo(SnapshotPath);
        if(file.LinkTarget is not null||file.Length>MaxBytes)throw new InvalidOperationException("MM_CACHE_INVALID");
        using var stream=new FileStream(SnapshotPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(stream.Length>MaxBytes)throw new InvalidOperationException("MM_CACHE_SIZE_LIMIT");
        var snapshot=JsonSerializer.Deserialize<MmPurchaseOrderSnapshot>(stream,Json)??throw new InvalidOperationException("MM_CACHE_INVALID");
        Validate(snapshot);return snapshot;
    }
    private void WriteCore(MmPurchaseOrderSnapshot snapshot)
    {
        if(readOnly)throw new InvalidOperationException("MM_CACHE_READ_ONLY");
        Validate(snapshot);
        var content=JsonSerializer.SerializeToUtf8Bytes(snapshot,Json);
        if(content.Length>MaxBytes)throw new InvalidOperationException("MM_CACHE_SIZE_LIMIT");
        var temp=Path.Combine(directory,"snapshot."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None};
            if(OperatingSystem.IsLinux())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using(var stream=new FileStream(temp,options)){stream.Write(content);stream.Flush(flushToDisk:true);}
            File.Move(temp,SnapshotPath,overwrite:true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public static void Validate(MmPurchaseOrderSnapshot value)
    {
        if(value.RuntimeMode is not("WINDOWS" or "VM")||value.IntervalSeconds is <1 or >3600||value.CreationCutoff!=MmCollectionPolicy.CreationCutoff||value.Host is null||value.Host.Length>100)throw new InvalidOperationException("MM_CACHE_POLICY_INVALID");
        // O par origem+sap-client tem de ser um ambiente APROVADO, e nao o DEV110 escrito aqui
        // dentro, que era o que esta linha exigia ate 05/11/2026: um cache de QAS120 era recusado
        // pela propria guarda, e o coletor de QAS nunca conseguiria gravar.
        //
        // O par e conferido JUNTO: a origem do DEV com o cliente do QAS nao e ambiente nenhum.
        if(value.Schema!=1||!FugaTES.Aplicacao.Configuracao.AmbientesSap.EhAprovado(value.Origin,value.Client)||value.Company!="3000"||value.Plants is null||!value.Plants.SequenceEqual(new[]{"3011","3008"})||value.Items is null||value.Items.Count>100000
            ||value.State is null||value.State.Length>80||!value.State.StartsWith("MM_",StringComparison.Ordinal)||value.State.Any(c=>!char.IsAsciiLetterUpper(c)&&c!='_'))throw new InvalidOperationException("MM_CACHE_IDENTITY_INVALID");
        if(value.GuardianRecords is null||value.GuardianRecords.Count>100000||value.GuardianState is null||!System.Text.RegularExpressions.Regex.IsMatch(value.GuardianState,"^MM_GUARDIAN_[A-Z_]{1,60}$"))throw new InvalidOperationException("MM_GUARDIAN_CACHE_INVALID");
        var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in value.Items)
            if(row is null||row.Empresa!="3000"||row.Centro is not("3011" or "3008")||row.EstadoSap!="05"||!keys.Add(row.Pedido+"/"+row.Item))throw new InvalidOperationException("MM_CACHE_SCOPE_INVALID");
        if(value.RuntimeMode=="VM")MmSnapshotDocumentGuard.Validate(value);
    }
}
