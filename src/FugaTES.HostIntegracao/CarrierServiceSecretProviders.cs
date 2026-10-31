using System.Net.Http.Headers;
using System.Text;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Aplicacao.Contratos.Recebimento;
namespace FugaTES.HostIntegracao;
public sealed class CarrierServiceSapAuthentication(IConfiguration config):IAutenticacaoSap
{
    public Task AplicarAsync(HttpRequestMessage request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(!OperatingSystem.IsWindows())throw new InvalidOperationException("CARRIER_WINDOWS_REQUIRED");
        if(request.RequestUri?.Scheme!="https")throw new InvalidOperationException("CARRIER_HTTPS_REQUIRED");
        var user=config["Carrier:Secrets:SapUser"]??throw new InvalidOperationException("CARRIER_SAP_CREDENTIAL_REQUIRED");
        var password=CarrierServiceRuntimeBootstrap.ReadCredential(config["Carrier:Secrets:SapCredentialTarget"]??throw new InvalidOperationException("CARRIER_SAP_CREDENTIAL_REQUIRED"),user);
        request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(user+":"+password)));return Task.CompletedTask;
    }
}
public sealed class CarrierServiceGuardianCredentials(IConfiguration config):ICredenciaisGuardianSet26Provider
{
    public Task<CredenciaisGuardianSet26> ObterAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(!OperatingSystem.IsWindows())throw new InvalidOperationException("CARRIER_WINDOWS_REQUIRED");
        var user=config["Carrier:Secrets:GuardianUser"]??throw new InvalidOperationException("CARRIER_GUARDIAN_CREDENTIAL_REQUIRED");
        var password=CarrierServiceRuntimeBootstrap.ReadCredential(config["Carrier:Secrets:GuardianCredentialTarget"]??throw new InvalidOperationException("CARRIER_GUARDIAN_CREDENTIAL_REQUIRED"),user);
        var signature=CarrierServiceRuntimeBootstrap.ReadCredential(config["Carrier:Secrets:GuardianSignatureTarget"]??throw new InvalidOperationException("CARRIER_GUARDIAN_SIGNATURE_REQUIRED"),config["Carrier:Secrets:GuardianSignatureUser"]??throw new InvalidOperationException("CARRIER_GUARDIAN_SIGNATURE_REQUIRED"));
        return Task.FromResult(new CredenciaisGuardianSet26(user,password,signature));
    }
}