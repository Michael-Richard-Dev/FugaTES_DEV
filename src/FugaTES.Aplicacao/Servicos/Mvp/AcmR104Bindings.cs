namespace FugaTES.Aplicacao.Servicos.Mvp;

// Approval references are supplied by the operational owner, never inferred from a code or example.
public sealed record AcmR104RestBinding(Guid IntegracaoId,long VersaoH4,string NumeroContratoSap,string Fingerprint,
    string CodigoTransportadora,string CodigoFluxo,string FonteTransportadora,string FonteFluxo);

public sealed record AcmR104CallbackBinding(string Environment,string Url,string SourceCidr,
    bool SourceAllowlistVerified,bool PreCadastroReturnRouteBindingVerified,string EvidenceReference);

public static class AcmR104CallbackPolicy
{
    public const string InternalHmlUrl="http://192.168.17.40/api/guardian/set26/callback/recebimento";
    public static bool Validar(string url,bool approved,AcmR104CallbackBinding? binding)
    {
        if(!approved || !Uri.TryCreate(url,UriKind.Absolute,out var uri) || !string.IsNullOrEmpty(uri.UserInfo))return false;
        if(uri.Scheme=="https")return true;
        // A documented weighing endpoint is not proof that it is the Cloud Prix return route.
        // No other HTTP host/port/path/query/fragment or environment is authorized by this exception.
        return url==InternalHmlUrl && binding is {Environment:"HML",SourceCidr:"192.168.17.91/32",
            SourceAllowlistVerified:true,PreCadastroReturnRouteBindingVerified:true} && binding.Url==url &&
            !string.IsNullOrWhiteSpace(binding.EvidenceReference);
    }
}
