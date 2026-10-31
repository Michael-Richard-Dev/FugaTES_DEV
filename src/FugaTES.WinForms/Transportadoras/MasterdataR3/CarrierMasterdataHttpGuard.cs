using FugaTES.Aplicacao.Transportadoras;

namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

// Exact selected identities. Authentication POST is allowed; business POST requires the live lease.
public sealed class CarrierMasterdataHttpGuard(Uri sap,Uri guardian,string bp,string code,string signature,
    Func<bool> writeGate,HttpMessageHandler inner):DelegatingHandler(inner)
{
    public int? CreateHttpStatus {get;private set;}
    public string? ReturnedCode {get;private set;}
    public long? ReturnedSequence {get;private set;}
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var u=request.RequestUri??throw new CarrierGatewayException("MASTERDATA_HTTP_URI_INVALID");
        bool safe=u.Scheme=="https"&&u.UserInfo.Length==0&&u.Fragment.Length==0;
        bool sapRead=u.GetLeftPart(UriPartial.Authority)==sap.GetLeftPart(UriPartial.Authority)&&request.Method==HttpMethod.Get&&
            u.AbsolutePath=="/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner('"+Uri.EscapeDataString(bp)+"')";
        bool origin=u.GetLeftPart(UriPartial.Authority)==guardian.GetLeftPart(UriPartial.Authority);
        bool read=origin&&request.Method==HttpMethod.Get&&u.AbsolutePath=="/api/v0/Guardian/Transportadora/"+Uri.EscapeDataString(code)&&
            u.Query=="?idAssinatura="+Uri.EscapeDataString(signature);
        bool auth=origin&&request.Method==HttpMethod.Post&&u.AbsolutePath=="/api/v2/CreateToken"&&u.Query.Length==0;
        bool write=origin&&request.Method==HttpMethod.Post&&u.AbsolutePath=="/api/v0/Guardian/Transportadora"&&u.Query.Length==0&&writeGate();
        if(!safe||!sapRead&&!read&&!auth&&!write)throw new CarrierGatewayException("MASTERDATA_HTTP_SCOPE_OR_WRITE_FORBIDDEN");
        var response=await base.SendAsync(request,ct).ConfigureAwait(false);
        if(write)
        {
            CreateHttpStatus=(int)response.StatusCode;
            try
            {
                using var json=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                ReturnedCode=GuardianSingleCreatePreparationR41.Text(json.RootElement,"codigo");
                var seq=json.RootElement.EnumerateObject().Where(x=>x.Name=="sequencial").ToArray();
                if(seq.Length==1&&seq[0].Value.ValueKind==System.Text.Json.JsonValueKind.Number&&seq[0].Value.TryGetInt64(out var n))ReturnedSequence=n;
            }
            catch(OperationCanceledException){response.Dispose();throw;}
            catch{ /* The proven adapter classifies the business response, never infer success here. */ }
        }
        return response;
    }
}
