using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.Integracao.Guardian;

public sealed record GuardianCarrierManualContract(bool ProfileVerified,bool SignatureVerified,string EvidenceReference,
    string LookupRoute,string CodeField,string LegalNameField,bool Scoped404Proven);
public sealed class GuardianCarrierManualReadOnlyAdapter(HttpClient http,GuardianHttpOptions options,GuardianTokenClient tokens,
    GuardianCarrierManualContract contract):IGuardianCarrierManualReadOnly
{
    public async Task<CarrierManualGuardian> ConsultarCodigoAsync(CarrierManualIdentity identity,CancellationToken ct)
    {
        CarrierManualGuardian Failed()=>new(CarrierLookupState.Failed,null,new Dictionary<string,string?>());
        if(!contract.ProfileVerified||!contract.SignatureVerified||string.IsNullOrWhiteSpace(contract.EvidenceReference)||!identity.Comprovada||
            options.BaseUrl is not {Scheme:"https"}||options.Timeout<=TimeSpan.Zero||options.Timeout>TimeSpan.FromMinutes(2))return Failed();
        // Signature ID is a separately approved GET scope, never taken from CreateToken.assinatura.
        var route=contract.LookupRoute.Replace("{bp}",Uri.EscapeDataString(identity.Codigo),StringComparison.Ordinal);
        if(route.Contains('{')||route.Contains('}')||route.Contains('\\')||route.StartsWith("//")||Uri.TryCreate(route,UriKind.Absolute,out _))return Failed();
        var uri=new Uri(options.BaseUrl,route);
        if(uri.GetLeftPart(UriPartial.Authority)!=options.BaseUrl.GetLeftPart(UriPartial.Authority)||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||
            uri.AbsolutePath!="/api/v0/Guardian/Transportadora/"+Uri.EscapeDataString(identity.Codigo))return Failed();
        var scope=uri.Query.TrimStart('?').Split('&').Where(p=>p.StartsWith("idAssinatura=",StringComparison.Ordinal)).ToArray();
        if(scope.Length!=1||Uri.UnescapeDataString(scope[0]["idAssinatura=".Length..])!=identity.IdAssinatura)return Failed();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(options.Timeout);
        var token=await tokens.ObterTokenAsync(timeout.Token).ConfigureAwait(false);
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
        if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)tokens.Invalidar(token);
        if(response.StatusCode==HttpStatusCode.NotFound)return new(CarrierLookupState.Absent,null,new Dictionary<string,string?>(),contract.Scoped404Proven);
        if(response.StatusCode!=HttpStatusCode.OK)return Failed();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false));
        var row=doc.RootElement;
        if(row.ValueKind==JsonValueKind.Array){if(row.GetArrayLength()!=1)return new(CarrierLookupState.Ambiguous,null,new Dictionary<string,string?>());row=row[0];}
        if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty(contract.CodeField,out var code)||code.ValueKind!=JsonValueKind.String)return Failed();
        var fields=new Dictionary<string,string?>(StringComparer.Ordinal);
        foreach(var property in row.EnumerateObject())if(property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null)fields[property.Name]=property.Value.ValueKind==JsonValueKind.Null?null:property.Value.GetString();
        var sequences=row.EnumerateObject().Where(p=>p.Name=="sequencial").ToArray();
        if(sequences.Length!=1)fields.Remove("sequencial");
        if(sequences.Length==1&&sequences[0].Value.ValueKind==JsonValueKind.Number&&sequences[0].Value.TryGetInt64(out var sequence)&&sequence>0)
            fields["sequencial"]=sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // sequencial is never interpreted as codigo or BP.
        return new(CarrierLookupState.Unique,code.GetString(),fields);
    }
}
