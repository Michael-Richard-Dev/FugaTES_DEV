namespace FugaTES.Aplicacao.Servicos.Mvp;

// Approval references are supplied by the operational owner, never inferred from a code or example.
public sealed record AcmR104RestBinding(Guid IntegracaoId,long VersaoH4,string NumeroContratoSap,string Fingerprint,
    string CodigoTransportadora,string CodigoFluxo,string FonteTransportadora,string FonteFluxo);

public sealed record AcmR104CallbackBinding(string Environment,string Url,string SourceCidr,
    bool SourceAllowlistVerified,bool PreCadastroReturnRouteBindingVerified,string EvidenceReference);

public static class AcmR104CallbackPolicy
{
    public const string InternalHmlUrl="http://192.168.17.40/api/guardian/set26/callback/recebimento";

    /// <summary>
    /// A rota de retorno do ACM no ambiente QAS120, aprovada pelo operador em 06/11/2026 junto com
    /// o par de rotas /recebimento-qas e /venda-qas.
    ///
    /// E o MESMO Guardian e o MESMO host interno: o que muda e so o caminho, porque os dois
    /// ambientes do FugaTES atendem na mesma maquina e o nginx usa correspondencia exata.
    /// </summary>
    public const string InternalHmlUrlQas="http://192.168.17.40/api/guardian/set26/callback/recebimento-qas";

    /// <summary>
    /// As UNICAS URLs em HTTP puro que esta politica aceita. Duas, desde 06/11/2026; eram uma.
    ///
    /// Acrescentar uma terceira e uma decisao do operador, nao um ajuste: cada entrada aqui e uma
    /// excecao a regra de que retorno so vai por HTTPS, e ela existe porque o Guardian nao alcanca
    /// esta maquina por TLS. As condicoes do binding valem igual para todas.
    /// </summary>
    public static readonly string[] UrlsHttpAprovadas=[InternalHmlUrl,InternalHmlUrlQas];

    public static bool Validar(string url,bool approved,AcmR104CallbackBinding? binding)
    {
        if(!approved || !Uri.TryCreate(url,UriKind.Absolute,out var uri) || !string.IsNullOrEmpty(uri.UserInfo))return false;
        if(uri.Scheme=="https")return true;
        // A documented weighing endpoint is not proof that it is the Cloud Prix return route.
        // No other HTTP host/port/path/query/fragment or environment is authorized by this exception.
        //
        // RISCO QUE ESTA EXCECAO NAO COBRE, e que o deploy precisa cobrir: as duas URLs tem o
        // mesmo Environment ("HML", que descreve o GUARDIAN, nao a gente) e o mesmo CIDR. Um
        // servico de QAS configurado com a URL de DEV passaria por aqui, e as pesagens dele iriam
        // para o banco do DEV. Quem amarra a URL ao ambiente do processo e quem publica o
        // servico, porque e la que a origem SAP e a rota de retorno sao definidas juntas.
        return Array.Exists(UrlsHttpAprovadas,aprovada=>url==aprovada) &&
            binding is {Environment:"HML",SourceCidr:"192.168.17.91/32",
            SourceAllowlistVerified:true,PreCadastroReturnRouteBindingVerified:true} && binding.Url==url &&
            !string.IsNullOrWhiteSpace(binding.EvidenceReference);
    }
}
