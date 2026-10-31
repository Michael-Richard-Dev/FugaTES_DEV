using System.Net.Http.Headers;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.CasosDeUso.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Autenticacao;
namespace FugaTES.Integracao.SAP.BusinessPartner;
public sealed record SapCarrierDeltaPage(IReadOnlyList<string> BusinessPartners, string? NextLink);
// Supply only the published, verified contract covering BP changes AND new CRM010 assignments.
// There is deliberately no default incremental field, endpoint, ordering or OData dialect.
public sealed record SapCarrierDeltaContract(Uri Origin, string EvidenceReference, bool IncludesBpChanges, bool IncludesNewCrm010Assignments,
    Func<CarrierCursor,Uri> BuildRequest, Func<JsonElement,SapCarrierDeltaPage> Decode, int PageLimit = 100, TimeSpan? Timeout = null);
public sealed class CarrierSapDiscoveryAdapter(HttpClient? http = null, IAutenticacaoSap? auth = null,
    ConsultarTransportadoraSapCasoDeUso? individual = null, SapCarrierDeltaContract? contract = null, TimeProvider? clock = null) : ISapCarrierDiscovery
{
    public async Task<CarrierPage> LerPaginaAsync(CarrierCursor cursor, CancellationToken ct)
    {
        if(contract is null || !contract.IncludesBpChanges || !contract.IncludesNewCrm010Assignments || string.IsNullOrWhiteSpace(contract.EvidenceReference))
            throw new CarrierGatewayException("SAP_CRM010_DELTA_CONTRACT_NOT_PROVEN");
        if(http is null || auth is null || individual is null || contract.PageLimit is <1 or >1000 || contract.Origin.Scheme!="https" || !contract.Origin.IsAbsoluteUri || contract.BuildRequest is null || contract.Decode is null)
            throw new CarrierGatewayException("SAP_DELTA_CONFIGURATION_INVALID");
        var upper=cursor.UpperBound ?? (clock??TimeProvider.System).GetUtcNow();
        if(cursor.Watermark>upper || (cursor.PageToken is not null && cursor.UpperBound is null))throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");
        var frozen=cursor with{UpperBound=upper};
        if(cursor.PageToken is not null)ValidateOrigin(new Uri(contract.Origin,cursor.PageToken));
        Uri endpoint;try{endpoint=contract.BuildRequest(frozen);}catch{throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");}
        ValidateOrigin(endpoint);
        if(cursor.VisitedPages is null || cursor.VisitedPages.Length>=1000 || cursor.VisitedPages.Any(v=>v is null || v.Length!=64 || !v.All(Uri.IsHexDigit)))
            throw new CarrierGatewayException("SAP_DELTA_PAGE_BOUND_EXCEEDED");
        var currentHash=PageHash(endpoint);
        if(cursor.VisitedPages.Contains(currentHash,StringComparer.Ordinal))throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");
        var visited=cursor.VisitedPages.Append(currentHash).ToArray();
        var timeoutValue=contract.Timeout??TimeSpan.FromSeconds(30);
        if(timeoutValue<=TimeSpan.Zero || timeoutValue>TimeSpan.FromMinutes(2))throw new CarrierGatewayException("SAP_DELTA_CONFIGURATION_INVALID");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(timeoutValue);
        using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await auth.AplicarAsync(request,timeout.Token);
        SapCarrierDeltaPage decoded;
        try
        {
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseContentRead,timeout.Token);
            if(!response.IsSuccessStatusCode)throw new CarrierGatewayException("SAP_DELTA_HTTP_"+(int)response.StatusCode);
            using var document=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));decoded=contract.Decode(document.RootElement);
        }
        catch(CarrierGatewayException){throw;}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{throw new CarrierGatewayException("SAP_DELTA_READ_FAILED");}
        if(decoded is null || decoded.BusinessPartners is null || decoded.BusinessPartners.Count>contract.PageLimit || decoded.BusinessPartners.Any(string.IsNullOrWhiteSpace))
            throw new CarrierGatewayException("SAP_DELTA_PAGE_INVALID");
        if(decoded.NextLink is not null)
        {
            if(string.IsNullOrWhiteSpace(decoded.NextLink))throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");
            Uri next;try{next=new Uri(contract.Origin,decoded.NextLink);}catch{throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");}
            ValidateOrigin(next);
            if(next==endpoint || visited.Contains(PageHash(next),StringComparer.Ordinal))throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");
        }
        var carriers=new List<CarrierPayload>();
        foreach(var bp in decoded.BusinessPartners.Distinct(StringComparer.Ordinal))
        {
            var result=await individual.ExecutarAsync(bp,ct);
            if(result.Status==StatusConsultaSap.CategoriaNaoCorrespondente || result.Status==StatusConsultaSap.NaoEncontrado)continue;
            if(result.Status!=StatusConsultaSap.Sucesso || result.Dados is null)throw new CarrierGatewayException("SAP_CARRIER_INDIVIDUAL_READ_FAILED");
            var data=result.Dados;
            if(data.BusinessPartner!=bp || data.CategoriaValidada!=CategoriaParceiroNegocio.Transportadora)throw new CarrierGatewayException("SAP_CARRIER_IDENTITY_INVALID");
            if(data.BloqueioSap!=SituacaoBloqueioSap.NaoBloqueado)continue;
            var name=data.Nome;var legal=data.NomeCompletoParceiroNegocio;
            if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(legal)||name.Length>50||legal.Length>50||bp.Length>20)
                throw new CarrierGatewayException("SAP_CARRIER_PAYLOAD_INVALID");
            carriers.Add(new(bp,name,legal));
        }
        bool end=decoded.NextLink is null;
        return new(carriers,end?new(upper,null,null):frozen with{PageToken=decoded.NextLink,VisitedPages=visited},end);
    }
    private static string PageHash(Uri uri)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
    private void ValidateOrigin(Uri uri)
    {
        if(!uri.IsAbsoluteUri || uri.Scheme!=contract!.Origin.Scheme || uri.Host!=contract.Origin.Host || uri.Port!=contract.Origin.Port || uri.UserInfo.Length!=0 || uri.Fragment.Length!=0)
            throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");
    }
}

