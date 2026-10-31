using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Integracao.Guardian;
namespace FugaTES.AcmSoapLookup.R33;
public static class LookupRunnerR33
{
    public const string AllowedCode="0000001031";
    public static bool ScopeValid(LookupRequestR33 r)=>r.Code==AllowedCode&&r.Center=="3011"&&r.ReferenceVersion==2;
    public static bool ProfileValid(LookupProfileR33 p,DateTimeOffset now)=>p is {Environment:"HML",ReadApproved:true,Endpoint:{IsAbsoluteUri:true,Scheme:"https"},ApprovedOrigin:{IsAbsoluteUri:true,Scheme:"https"}}
        &&!string.IsNullOrWhiteSpace(p.ApprovalReference)&&p.UntilUtc>now&&p.TimeoutSeconds is >=1 and <=60&&
        p.Endpoint.UserInfo.Length==0&&p.Endpoint.Fragment.Length==0&&p.ApprovedOrigin.UserInfo.Length==0&&
        p.ApprovedOrigin.Query.Length==0&&p.ApprovedOrigin.Fragment.Length==0&&
        p.Endpoint.GetLeftPart(UriPartial.Authority)==p.ApprovedOrigin.GetLeftPart(UriPartial.Authority);
    public static async Task<LookupResultR33> RunAsync(LookupRequestR33 r,LookupProfileR33 p,HttpMessageHandler transport,CancellationToken ct=default)
    {
        if(!ScopeValid(r))return new("BLOCKED","LOCAL_CONTRACT_SCOPE_INVALID");
        if(!ProfileValid(p,DateTimeOffset.UtcNow))return new("BLOCKED","SOAP_READ_PROFILE_NOT_APPROVED");
        if(ct.IsCancellationRequested)return new("BLOCKED","LOOKUP_CANCELLED_BEFORE_SEND");
        try
        {
            using var client=new HttpClient(new ConsultaOnlyTransportR33(p.Endpoint!,transport));
            var soap=new GuardianContratoSoapClient(client,new GuardianHttpOptions{ContratoSoapEndpoint=p.Endpoint,Timeout=TimeSpan.FromSeconds(p.TimeoutSeconds)});
            var result=await soap.ConsultarAsync(AllowedCode,ct).ConfigureAwait(false);
            return result.Estado==EstadoConsultaContratoGuardian.Existente&&result.Contrato?.Codigo==AllowedCode
                ?new("LOOKUP_CONFIRMED","CONTRACT_CODE_CONFIRMED_CENTRE_VERSION_UNVERIFIED",AllowedCode)
                :new("INDETERMINATE","SOAP_RESULT_NOT_CONCLUSIVE");
        }
        catch{return new("INDETERMINATE","SOAP_LOOKUP_FAILED_SANITIZED");}
    }
}
