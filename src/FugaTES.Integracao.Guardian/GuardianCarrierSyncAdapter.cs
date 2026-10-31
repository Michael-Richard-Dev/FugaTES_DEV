using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
namespace FugaTES.Integracao.Guardian;
// Wire codecs/absence semantics come from the proven HML signature; no guessed endpoints/JSON schema.
public sealed record GuardianCarrierHmlContract(bool ProfileVerified, bool SignatureVerified, string EvidenceReference,
    string LookupRoute, string CreateRoute, string UpdateRoute, string ReadbackRoute, int? AbsenceHttpStatus,
    Func<JsonElement,CarrierLookup> DecodeLookup, Func<CarrierWriteAttempt,object> EncodeWrite, Func<JsonElement,string?> DecodeWriteCode);
public sealed class GuardianCarrierSyncAdapter(HttpClient? http = null, GuardianHttpOptions? options = null, GuardianTokenClient? tokens = null,
    GuardianCarrierHmlContract? contract = null, Func<CarrierWriteAttempt,bool>? authorize = null) : IGuardianCarrierGateway
{
    private bool Proven => contract is { ProfileVerified:true,SignatureVerified:true } && !string.IsNullOrWhiteSpace(contract.EvidenceReference)
        && http is not null && tokens is not null && options?.BaseUrl is {IsAbsoluteUri:true,Scheme:"https"} && options.Timeout>TimeSpan.Zero && options.Timeout<=TimeSpan.FromMinutes(2)
        && contract.DecodeLookup is not null && contract.EncodeWrite is not null && contract.DecodeWriteCode is not null;
    public Task<CarrierLookup> ConsultarAsync(string bp,CancellationToken ct)=>Lookup(bp,false,ct);
    public Task<CarrierLookup> LerConfirmacaoAsync(string codigo,CancellationToken ct)=>Lookup(codigo,true,ct);
    private async Task<CarrierLookup> Lookup(string identity,bool readback,CancellationToken ct)
    {
        if(!Proven)return Failed("GUARDIAN_CARRIER_HML_CONTRACT_NOT_PROVEN");
        if(string.IsNullOrWhiteSpace(identity))return Failed("GUARDIAN_CARRIER_IDENTITY_INVALID");
        Uri uri;try{uri=Route(readback?contract!.ReadbackRoute:contract!.LookupRoute,identity,readback?"{code}":"{bp}");}catch{return Failed("GUARDIAN_CARRIER_ROUTE_INVALID");}
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(options!.Timeout);
        try
        {
            var token=await tokens!.ObterTokenAsync(timeout.Token);
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var response=await http!.SendAsync(request,HttpCompletionOption.ResponseContentRead,timeout.Token);
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)tokens.Invalidar(token);
            if(!readback && contract!.AbsenceHttpStatus is int absent && absent==404 && (int)response.StatusCode==absent)return new(CarrierLookupState.Absent);
            if(!response.IsSuccessStatusCode)return Failed("GUARDIAN_CARRIER_GET_HTTP_"+(int)response.StatusCode);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
            var result=contract!.DecodeLookup(json.RootElement);
            // Successful HTTP alone is never absence. Empty/ambiguous responses require explicit handling.
            if(result.State==CarrierLookupState.Absent)return Failed("GUARDIAN_CARRIER_UNPROVEN_ABSENCE");
            if(result.State==CarrierLookupState.Unique && (result.Payload is null || string.IsNullOrWhiteSpace(result.GuardianCode)
                || (readback?result.GuardianCode!=identity:result.Payload.BusinessPartner!=identity)))return Failed("GUARDIAN_CARRIER_IDENTITY_INVALID");
            return result with{ErrorCode=result.State==CarrierLookupState.Failed?"GUARDIAN_CARRIER_RESPONSE_INVALID":null};
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{return Failed("GUARDIAN_CARRIER_GET_FAILED");}
    }
    // R13: Guardian PUT uses the documented collection endpoint. Never append a Guardian code.
    // Only the approved exact route is exempted; legacy tokenized routes retain Route() validation.
    private Uri ResolveApprovedCollectionUpdateRoute(string template, string identity, string marker)
    {
        if (!string.Equals(template, "/api/v0/Guardian/Transportadora", StringComparison.Ordinal))
            return Route(template, identity, marker);
        return options!.ResolveEndpoint(template, nameof(GuardianCarrierHmlContract.UpdateRoute));
    }
    public async Task<CarrierWriteResult> GravarAsync(CarrierWriteAttempt attempt,CancellationToken ct)
    {
        if(!Proven)return Rejected("GUARDIAN_CARRIER_HML_CONTRACT_NOT_PROVEN");
        if(attempt.Id==Guid.Empty || attempt.Action is not("CREATE" or "UPDATE") || attempt.Payload is null || attempt.BusinessPartner!=attempt.Payload.BusinessPartner
            || string.IsNullOrWhiteSpace(attempt.BusinessPartner)||attempt.BusinessPartner.Length>20
            || string.IsNullOrWhiteSpace(attempt.Payload.Name)||attempt.Payload.Name.Length>50
            || string.IsNullOrWhiteSpace(attempt.Payload.LegalName)||attempt.Payload.LegalName.Length>50
            || (attempt.Action=="UPDATE" && string.IsNullOrWhiteSpace(attempt.GuardianCode)))return Rejected("GUARDIAN_CARRIER_PAYLOAD_INVALID");
        if(authorize is null || !authorize(attempt))return Rejected("GUARDIAN_CARRIER_WRITE_NOT_AUTHORIZED");
        Uri uri;object body;
        try{uri=(attempt.Action=="CREATE" ? Route(contract!.CreateRoute, attempt.GuardianCode??attempt.BusinessPartner, "{code}") : ResolveApprovedCollectionUpdateRoute(contract!.UpdateRoute, attempt.GuardianCode??attempt.BusinessPartner, "{code}"));body=contract!.EncodeWrite(attempt);}
        catch{return Rejected("GUARDIAN_CARRIER_WIRE_INVALID");}
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(options!.Timeout);
        string token;
        try{token=await tokens!.ObterTokenAsync(timeout.Token);}catch{return Rejected("GUARDIAN_CARRIER_AUTH_FAILED");}
        // Cancellation before dispatch is known not sent. After dispatch every network failure is indeterminate.
        if(timeout.IsCancellationRequested)return Rejected("GUARDIAN_CARRIER_WRITE_NOT_SENT");
        if(!authorize(attempt))return Rejected("GUARDIAN_CARRIER_WRITE_NOT_AUTHORIZED");
        try
        {
            using var request=new HttpRequestMessage(attempt.Action=="CREATE"?HttpMethod.Post:HttpMethod.Put,uri){Content=JsonContent.Create(body)};
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var response=await http!.SendAsync(request,HttpCompletionOption.ResponseContentRead,timeout.Token);
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)tokens.Invalidar(token);
            if(!response.IsSuccessStatusCode)
            {
                if((int)response.StatusCode>=500 || response.StatusCode==HttpStatusCode.RequestTimeout)return Indeterminate();
                return Rejected("GUARDIAN_CARRIER_WRITE_HTTP_"+(int)response.StatusCode);
            }
            string? code=attempt.GuardianCode;
            if(attempt.Action=="CREATE")
            {
                using var json=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));code=contract!.DecodeWriteCode(json.RootElement);
                if(string.IsNullOrWhiteSpace(code))return Indeterminate();
            }
            return new(true,false,code){HttpStatusCode=(int)response.StatusCode};
        }
        catch{return Indeterminate();}
    }
    private Uri Route(string route,string identity,string placeholder)
    {
        if(string.IsNullOrWhiteSpace(route)||route.StartsWith("//",StringComparison.Ordinal)||route.Contains('\\')||Uri.TryCreate(route,UriKind.Absolute,out _))throw new CarrierGatewayException("GUARDIAN_CARRIER_ROUTE_INVALID");
        var value=route.Replace(placeholder,Uri.EscapeDataString(identity),StringComparison.Ordinal);
        if(value.Contains('{')||value.Contains('}'))throw new CarrierGatewayException("GUARDIAN_CARRIER_ROUTE_INVALID");
        var uri=new Uri(options!.BaseUrl!,value);
        if(uri.Scheme!=options.BaseUrl!.Scheme||uri.Host!=options.BaseUrl.Host||uri.Port!=options.BaseUrl.Port||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw new CarrierGatewayException("GUARDIAN_CARRIER_ROUTE_INVALID");
        return uri;
    }
    private static CarrierLookup Failed(string code)=>new(CarrierLookupState.Failed,ErrorCode:code);
    private static CarrierWriteResult Rejected(string code)=>new(false,false,ErrorCode:code);
    private static CarrierWriteResult Indeterminate()=>new(false,true,ErrorCode:"GUARDIAN_CARRIER_WRITE_INDETERMINATE");
}

