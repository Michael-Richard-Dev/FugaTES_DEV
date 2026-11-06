using System.Net.Http.Headers;
using System.Text;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
namespace FugaTES.HostIntegracao;

public sealed class CarrierSystemdCredentials(string directory)
{
    public static readonly IReadOnlyList<string> RequiredNames=Array.AsReadOnly(new[]{"carrier-hml-postgres-password","carrier-hml-guardian-email","carrier-hml-guardian-password","carrier-hml-guardian-signature","carrier-hml-sap-user","carrier-hml-sap-password"});
    public static void ValidateName(string name)
    {if(!RequiredNames.Contains(name,StringComparer.Ordinal))throw new CarrierServiceStartupException("CARRIER_LINUX_CREDENTIAL_ID_INVALID");}
    public static void ValidateMode(UnixFileMode mode)=>ValidateMode(mode,false);
    public static void ValidateMode(UnixFileMode mode,bool approvedToledoSystemdScope)
    {
        var groupRead=UnixFileMode.UserRead|UnixFileMode.GroupRead;
        if(mode!=UnixFileMode.UserRead&&!(approvedToledoSystemdScope&&mode==groupRead))
            throw new CarrierServiceStartupException("CARRIER_LINUX_CREDENTIAL_PERMISSIONS_INVALID");
    }
    public static bool IsApprovedToledoRuntimeDirectory(string name,string directory,string? runtimeDirectory,
        UnixFileMode directoryMode,UnixFileMode parentMode,bool hasLinks)
    {
        const string expected="/run/credentials/fugates-toledo-callback.service";
        var owner=UnixFileMode.UserRead|UnixFileMode.UserExecute;
        var serviceGroup=owner|UnixFileMode.GroupRead|UnixFileMode.GroupExecute;
        return (name is "carrier-hml-sap-user" or "carrier-hml-sap-password") &&
            directory==expected&&runtimeDirectory==expected&&!hasLinks&&
            (directoryMode==owner||directoryMode==serviceGroup)&&
            (parentMode&(UnixFileMode.GroupWrite|UnixFileMode.OtherWrite))==0;
    }
    public static bool IsApprovedMmRuntimeDirectory(string name,string directory,string? runtimeDirectory,
        UnixFileMode directoryMode,UnixFileMode parentMode,bool hasLinks)
    {
        const string expected="/run/credentials/fugates-mm-purchase-orders.service";
        var owner=UnixFileMode.UserRead|UnixFileMode.UserExecute;
        var group=owner|UnixFileMode.GroupRead|UnixFileMode.GroupExecute;
        return RequiredNames.Contains(name,StringComparer.Ordinal)&&directory==expected&&runtimeDirectory==expected&&!hasLinks&&
            (directoryMode==owner||directoryMode==group)&&(parentMode&(UnixFileMode.GroupWrite|UnixFileMode.OtherWrite))==0;
    }
    public string Read(string name)
    {
        ValidateName(name);
        if(!OperatingSystem.IsLinux())throw new CarrierServiceStartupException("CARRIER_LINUX_REQUIRED");
        try
        {
            if(!Path.IsPathFullyQualified(directory)||directory.Contains("/../",StringComparison.Ordinal)||directory.Contains("/./",StringComparison.Ordinal)||new DirectoryInfo(directory).LinkTarget is not null)
                throw new CarrierServiceStartupException("CARRIER_LINUX_CREDENTIAL_DIRECTORY_INVALID");
            var path=Path.Combine(directory,name);var file=new FileInfo(path);
            if(!file.Exists||file.LinkTarget is not null||file.Length is <1 or >16384)throw new CarrierServiceStartupException("CARRIER_LINUX_CREDENTIAL_MISSING_OR_INVALID");
            var mode=File.GetUnixFileMode(path);var approvedRuntime=false;
            if(mode==(UnixFileMode.UserRead|UnixFileMode.GroupRead))
            {
                var folder=new DirectoryInfo(directory);var parent=folder.Parent;
                if(parent is not null)approvedRuntime=IsApprovedToledoRuntimeDirectory(name,directory,
                    Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY"),File.GetUnixFileMode(directory),
                    File.GetUnixFileMode(parent.FullName),folder.LinkTarget is not null||parent.LinkTarget is not null) || IsApprovedMmRuntimeDirectory(name,directory,
                    Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY"),File.GetUnixFileMode(directory),
                    File.GetUnixFileMode(parent.FullName),folder.LinkTarget is not null||parent.LinkTarget is not null);
            }
            ValidateMode(mode,approvedRuntime);
            var secret=File.ReadAllText(path,new UTF8Encoding(false,true));
            if(string.IsNullOrWhiteSpace(secret)||secret.Contains('\0'))throw new CarrierServiceStartupException("CARRIER_LINUX_CREDENTIAL_MISSING_OR_INVALID");
            return secret;
        }
        catch(CarrierServiceStartupException){throw;}
        catch{throw new CarrierServiceStartupException("CARRIER_LINUX_CREDENTIAL_UNAVAILABLE");}
    }
    public void ValidateRequired(){foreach(var name in RequiredNames)_=Read(name);}
}

public sealed class CarrierLinuxGuardianCredentials:ICredenciaisGuardianSet26Provider
{
    private readonly CarrierSystemdCredentials credentials;
    public CarrierLinuxGuardianCredentials(CarrierLinuxRuntimeBootstrap runtime):this(runtime.Credentials){}
    public CarrierLinuxGuardianCredentials(CarrierSystemdCredentials credentials){this.credentials=credentials;}
    public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new CredenciaisGuardianSet26(credentials.Read("carrier-hml-guardian-email"),credentials.Read("carrier-hml-guardian-password"),credentials.Read("carrier-hml-guardian-signature")));
    }
}
public sealed class CarrierLinuxSapAuthentication:IAutenticacaoSap
{
    private readonly Uri approved;
    private readonly CarrierSystemdCredentials credentials;
    private readonly string? expectedUser;
    public CarrierLinuxSapAuthentication(CarrierLinuxRuntimeBootstrap runtime)
        :this(new Uri(runtime.Profile.Sap.BaseUrl),runtime.Credentials) { }
    // Reuse the same protected SAP credential IDs without bootstrapping Guardian/MasterData.
    public CarrierLinuxSapAuthentication(Uri approved,CarrierSystemdCredentials credentials,string? expectedUser=null)
    {
        if(!approved.IsAbsoluteUri||approved.Scheme!="https"||approved.UserInfo.Length!=0)
            throw new CarrierServiceStartupException("CARRIER_LINUX_SAP_ORIGIN_INVALID");
        this.approved=approved;this.credentials=credentials;this.expectedUser=expectedUser;
    }
    public Task AplicarAsync(HttpRequestMessage request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.RequestUri is not {Scheme:"https"} endpoint||endpoint.GetLeftPart(UriPartial.Authority)!=approved.GetLeftPart(UriPartial.Authority)||endpoint.UserInfo.Length!=0)
            throw new CarrierServiceStartupException("CARRIER_LINUX_SAP_ORIGIN_INVALID");
        var user=credentials.Read("carrier-hml-sap-user");
        if(expectedUser is not null&&user!=expectedUser)throw new CarrierServiceStartupException("CARRIER_LINUX_SAP_USER_MISMATCH");
        var password=credentials.Read("carrier-hml-sap-password");
        request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(user+":"+password)));return Task.CompletedTask;
    }
}