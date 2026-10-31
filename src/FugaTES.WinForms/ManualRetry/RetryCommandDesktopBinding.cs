using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Infraestrutura.ManualRetry;

namespace FugaTES.WinForms.ManualRetry;

public sealed record RetryCommandDesktopProfile
{
    public bool Enabled{get;init;}
    public bool Homologated{get;init;}
    public string ApprovalReference{get;init;}="";
    public string OperatorSid{get;init;}="";
    public DateTimeOffset UntilUtc{get;init;}
    public string CommandCredentialFile{get;init;}="";
    public LocalRetryCommandTunnelOptions? Tunnel{get;init;}

    // Local transport admission only. Business approval is resolved exclusively by the server.
    public AcmRetryCommandBinding Validate(string currentSid,DateTimeOffset now,string applicationDirectory,string readKeyFile)
    {
        if(!Enabled||!Homologated||!AcmManualRetryPolicy.Evidence(ApprovalReference)||OperatorSid!=currentSid||
            !currentSid.StartsWith("S-1-5-21-",StringComparison.Ordinal)||UntilUtc.Offset!=TimeSpan.Zero||now>=UntilUtc||Tunnel is null)
            throw new InvalidOperationException("RETRY_COMMAND_PROFILE_REJECTED");
        LocalRetryCommandTunnel.BuildArguments(Tunnel);
        RetryCommandCredential.ValidatePath(CommandCredentialFile,applicationDirectory,readKeyFile,Tunnel.IdentityFile,Tunnel.KnownHostsFile);
        return new(new Uri("http://127.0.0.1:18763"),true,ApprovalReference,currentSid,UntilUtc);
    }
}

public static class RetryCommandCredential
{
    public static void ValidatePath(string path,string applicationDirectory,params string[] otherCredentials)
    {
        if(!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\",StringComparison.Ordinal)||path.Skip(2).Contains(':')||path.Any(char.IsControl))
            throw new InvalidOperationException("RETRY_COMMAND_CREDENTIAL_PATH_REJECTED");
        var full=Path.GetFullPath(path);
        var package=Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        if(full.Equals(package,StringComparison.OrdinalIgnoreCase)||full.StartsWith(package+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||
            otherCredentials.Any(other=>!string.IsNullOrWhiteSpace(other)&&Path.IsPathFullyQualified(other)&&full.Equals(Path.GetFullPath(other),StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("RETRY_COMMAND_CREDENTIAL_PATH_REJECTED");
        for(var parent=new FileInfo(full).Directory;parent is not null;parent=parent.Parent)
            if(HasGitMetadata(parent.FullName))
                throw new InvalidOperationException("RETRY_COMMAND_CREDENTIAL_PATH_REJECTED");
    }

    static bool HasGitMetadata(string directory)
    {
        try{_ = File.GetAttributes(Path.Combine(directory,".git"));return true;}
        catch(FileNotFoundException){return false;}
        catch(DirectoryNotFoundException){return false;}
        // Access-denied metadata is not proof that this path is outside a checkout.
    }
    public static void ValidateAcl(FileSecurity acl,string currentSid)
    {
        var rules=acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var allowed=new HashSet<string>(StringComparer.Ordinal){currentSid,"S-1-5-18"};
        if(!acl.AreAccessRulesProtected||rules.Any(rule=>rule.IsInherited||!allowed.Contains(rule.IdentityReference.Value))||
            !rules.Any(rule=>rule.AccessControlType==AccessControlType.Allow&&rule.IdentityReference.Value==currentSid&&(rule.FileSystemRights&FileSystemRights.ReadData)!=0)||
            rules.Any(rule=>rule.AccessControlType==AccessControlType.Deny&&rule.IdentityReference.Value==currentSid&&(rule.FileSystemRights&FileSystemRights.ReadData)!=0))
            throw new InvalidOperationException("RETRY_COMMAND_CREDENTIAL_ACL_REJECTED");
    }

    // Invoked only by the legitimate desktop runtime, after the profile/current SID/path checks.
    internal static string Load(string path,string currentSid)
    {
        for(FileSystemInfo? entry=new FileInfo(path);entry is not null;entry=entry is FileInfo file?file.Directory:((DirectoryInfo)entry).Parent)
            if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("RETRY_COMMAND_CREDENTIAL_LINK_REJECTED");
        var info=new FileInfo(path);
        if((info.Attributes&FileAttributes.Directory)!=0||info.Length is <32 or >4096)
            throw new InvalidOperationException("RETRY_COMMAND_CREDENTIAL_REJECTED");
        using var stream=new FileStream(info.FullName,FileMode.Open,FileAccess.Read,FileShare.Read);
        var acl=info.GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
        ValidateAcl(acl,currentSid);
        var owner=acl.GetOwner(typeof(SecurityIdentifier))?.Value;
        if(owner!=currentSid&&owner!="S-1-5-18")throw new InvalidOperationException("RETRY_COMMAND_CREDENTIAL_OWNER_REJECTED");
        using var reader=new StreamReader(stream,new UTF8Encoding(false,true),true,4096);
        return reader.ReadToEnd().Trim();
    }
}

// Owned by UnifiedDesktopSession, never by an individual form. No Guardian/SAP or DB client is composed here.
public sealed class RetryCommandDesktopBinding:IDisposable
{
    readonly AuthenticatedAcmRetryCommandClient? command;
    readonly LocalRetryCommandTunnel? tunnel;
    public IAcmManualRetryCommandClient Client{get;}
    RetryCommandDesktopBinding(IAcmManualRetryCommandClient client,LocalRetryCommandTunnel? tunnel=null)
    {Client=client;command=client as AuthenticatedAcmRetryCommandClient;this.tunnel=tunnel;}

    public static RetryCommandDesktopBinding Load(string applicationDirectory,string readKeyFile,Func<Guid,CancellationToken,Task<MonitorAcmDetalhe>>? read=null)
    {
        LocalRetryCommandTunnel? tunnel=null;
        try
        {
            var path=Path.Combine(applicationDirectory,"FugaTES.retry-command.json");
            if(!File.Exists(path))return new(new ReadOnlyAcmManualRetryClient(read));
            var profileInfo=new FileInfo(path);
            if(profileInfo.Length is <2 or >16384||(profileInfo.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("RETRY_COMMAND_PROFILE_REJECTED");
            var profile=JsonSerializer.Deserialize<RetryCommandDesktopProfile>(File.ReadAllText(path));
            if(profile is not{Enabled:true})return new(new ReadOnlyAcmManualRetryClient(read));
            var sid=WindowsIdentity.GetCurrent().User?.Value??"";
            var binding=profile.Validate(sid,TimeProvider.System.GetUtcNow(),applicationDirectory,readKeyFile);
            var credential=RetryCommandCredential.Load(profile.CommandCredentialFile,sid);
            tunnel=new(profile.Tunnel!);
            var client=new AuthenticatedAcmRetryCommandClient(binding,credential,TimeProvider.System,prepare:tunnel.EnsureAsync);
            return new(client,tunnel);
        }
        catch
        {
            tunnel?.Dispose();
            // Fail closed without rendering raw authentication/path/transport diagnostics or secret values.
            return new(new ReadOnlyAcmManualRetryClient(read));
        }
    }
    public void Dispose(){command?.Dispose();tunnel?.Dispose();}
}



