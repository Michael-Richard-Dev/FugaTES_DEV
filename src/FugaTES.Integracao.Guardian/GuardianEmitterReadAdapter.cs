using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Guardian;
namespace FugaTES.Integracao.Guardian;

/// <summary>GET de emissor existente. Não cadastra, não altera, não usa sequencial como código.</summary>
public sealed class GuardianEmitterReadAdapter(HttpClient http,GuardianHttpOptions options,GuardianTokenClient tokens,int idAssinatura):IGuardianEmitterRead
{
    public async Task<GuardianEmitterReadResult> ConsultarAsync(string codigo,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();int? status=null;
        GuardianEmitterReadResult Block(string reason)=>new(false,"",0,idAssinatura,status,reason);
        if(options.BaseUrl is not{IsAbsoluteUri:true,Scheme:"https"} origin||origin.GetLeftPart(UriPartial.Authority)!="https://guardian.cloudprix.com.br"||origin.UserInfo.Length!=0||origin.AbsolutePath!="/"||origin.Query.Length!=0||origin.Fragment.Length!=0||idAssinatura<=0||options.Timeout<=TimeSpan.Zero||options.Timeout>TimeSpan.FromMinutes(2))return Block("R171_EMITTER_PROFILE_INVALID");
        if(string.IsNullOrEmpty(codigo)||codigo.Length>20||codigo[0]=='0'||!codigo.All(char.IsAsciiDigit))return Block("R171_EMITTER_CODE_INVALID");
        var endpoint=new Uri(origin,"/api/v0/Guardian/Emissor/"+codigo+"?idAssinatura="+idAssinatura.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(options.Timeout<TimeSpan.FromSeconds(20)?options.Timeout:TimeSpan.FromSeconds(20));
        try
        {
            var token=await tokens.ObterTokenAsync(deadline.Token);
            using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);status=(int)response.StatusCode;
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)tokens.Invalidar(token);
            if(status==404)return Block("R171_EMITTER_NOT_FOUND");
            if(status==401)return Block("R171_EMITTER_AUTHENTICATION_FAILED");
            if(status==403)return Block("R171_EMITTER_AUTHORIZATION_FAILED");
            if(status!=200)return Block("R171_EMITTER_LOOKUP_UNVERIFIED");
            const int limit=262144;
            if(response.Content.Headers.ContentLength>limit)return Block("R171_EMITTER_RESPONSE_INVALID");
            await using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);using var memory=new MemoryStream();var buffer=new byte[8192];int count;
            while((count=await stream.ReadAsync(buffer,deadline.Token))!=0){if(memory.Length+count>limit)return Block("R171_EMITTER_RESPONSE_INVALID");memory.Write(buffer,0,count);}
            using var doc=JsonDocument.Parse(memory.ToArray(),new JsonDocumentOptions{MaxDepth=16});var row=doc.RootElement;
            if(row.ValueKind!=JsonValueKind.Object)return Block("R171_EMITTER_RESPONSE_INVALID");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in row.EnumerateObject())if(!names.Add(property.Name))return Block("R171_EMITTER_RESPONSE_INVALID");
            if(!row.TryGetProperty("codigo",out var code)||code.ValueKind!=JsonValueKind.String||!row.TryGetProperty("sequencial",out var sequence)||!sequence.TryGetInt64(out var number)||number<=0||!row.TryGetProperty("estado",out var state)||!state.TryGetInt32(out var active)||active is not(0 or 1))return Block("R171_EMITTER_RESPONSE_INVALID");
            if(code.GetString()!=codigo)return Block("R171_EMITTER_IDENTITY_MISMATCH");
            if(row.TryGetProperty("idAssinatura",out var scope)&&(!scope.TryGetInt32(out var returnedScope)||returnedScope!=idAssinatura))return Block("R171_EMITTER_SCOPE_MISMATCH");
            if(active!=1)return Block("R171_EMITTER_INACTIVE");
            return new(true,code.GetString()!,number,idAssinatura,status,"R171_EMITTER_CONFIRMED");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(GuardianHttpException e){status=e.HttpStatusCode;return Block(status==401?"R171_EMITTER_AUTHENTICATION_FAILED":status==403?"R171_EMITTER_AUTHORIZATION_FAILED":"R171_EMITTER_LOOKUP_UNVERIFIED");}
        catch(JsonException){return Block("R171_EMITTER_RESPONSE_INVALID");}
        catch(InvalidOperationException){return Block("R171_EMITTER_RESPONSE_INVALID");}
        catch{return Block("R171_EMITTER_LOOKUP_UNVERIFIED");}
    }
}

// The lookup has a GET-only handler and redirects disabled; existing business transport is unchanged.
public sealed class GuardianEmitterReadTransport:IDisposable
{
    public HttpClient Client{get;}=new(new ReadOnlyHandler(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false})){Timeout=Timeout.InfiniteTimeSpan};
    public void Dispose()=>Client.Dispose();
    private sealed class ReadOnlyHandler(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.Method!=HttpMethod.Get||request.RequestUri is not {Scheme:"https"} uri||uri.GetLeftPart(UriPartial.Authority)!="https://guardian.cloudprix.com.br"||!uri.AbsolutePath.StartsWith("/api/v0/Guardian/Emissor/",StringComparison.Ordinal))throw new InvalidOperationException("R171_READ_ONLY_ROUTE_REQUIRED");
            return base.SendAsync(request,ct);
        }
    }
}
