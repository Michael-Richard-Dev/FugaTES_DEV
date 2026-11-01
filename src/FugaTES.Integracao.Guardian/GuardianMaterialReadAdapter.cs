using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Guardian;
namespace FugaTES.Integracao.Guardian;

// Documented GET Item/{codigoItem}?idAssinatura. No item creation or modification.
public sealed class GuardianMaterialReadAdapter(HttpClient http,GuardianHttpOptions options,GuardianTokenClient tokens,int idAssinatura):IGuardianMaterialRead
{
 public async Task<GuardianMaterialReadResult> ConsultarAsync(string codigo,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();int? status=null;
  GuardianMaterialReadResult Block(string code)=>new(false,"",0,idAssinatura,status,code);
  if(options.BaseUrl is not{IsAbsoluteUri:true,Scheme:"https"} origin||origin.GetLeftPart(UriPartial.Authority)!="https://guardian.cloudprix.com.br"||origin.UserInfo.Length!=0||origin.AbsolutePath!="/"||origin.Query.Length!=0||origin.Fragment.Length!=0||idAssinatura<=0||options.Timeout<=TimeSpan.Zero||options.Timeout>TimeSpan.FromMinutes(2))return Block("ACM_MATERIAL_PROFILE_INVALID");
  if(!GuardianMaterialCode.IsValid(codigo))return Block("ACM_MATERIAL_CODE_INVALID");
  var endpoint=new Uri(origin,"/api/v0/Guardian/Item/"+Uri.EscapeDataString(codigo)+"?idAssinatura="+idAssinatura.ToString(System.Globalization.CultureInfo.InvariantCulture));
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(options.Timeout<TimeSpan.FromSeconds(20)?options.Timeout:TimeSpan.FromSeconds(20));
  try
  {
   var token=await tokens.ObterTokenAsync(deadline.Token);
   using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
   using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);status=(int)response.StatusCode;
   if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)tokens.Invalidar(token);
   if(status==404)return Block("ACM_MATERIAL_NOT_FOUND");
   if(status==401)return Block("ACM_MATERIAL_AUTHENTICATION_FAILED");
   if(status==403)return Block("ACM_MATERIAL_AUTHORIZATION_FAILED");
   if(status!=200)return Block("ACM_MATERIAL_LOOKUP_UNVERIFIED");
   const int limit=262144;if(response.Content.Headers.ContentLength>limit)return Block("ACM_MATERIAL_RESPONSE_INVALID");
   await using var stream=await response.Content.ReadAsStreamAsync(deadline.Token);using var memory=new MemoryStream();var buffer=new byte[8192];int count;
   while((count=await stream.ReadAsync(buffer,deadline.Token))!=0){if(memory.Length+count>limit)return Block("ACM_MATERIAL_RESPONSE_INVALID");memory.Write(buffer,0,count);}
   using var json=JsonDocument.Parse(memory.ToArray(),new JsonDocumentOptions{MaxDepth=16});var row=json.RootElement;
   if(row.ValueKind!=JsonValueKind.Object)return Block("ACM_MATERIAL_RESPONSE_INVALID");
   var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var property in row.EnumerateObject())if(!names.Add(property.Name))return Block("ACM_MATERIAL_RESPONSE_INVALID");
   if(!row.TryGetProperty("codigo",out var code)||code.ValueKind!=JsonValueKind.String||!row.TryGetProperty("sequencial",out var seq)||!seq.TryGetInt64(out var number)||number<=0||
    !row.TryGetProperty("estado",out var state)||!state.TryGetInt32(out var active)||active is not(0 or 1)||
    !row.TryGetProperty("tipo",out var kind)||!kind.TryGetInt32(out var type)||type is <20 or >23||
    !row.TryGetProperty("nome",out var name)||name.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(name.GetString())||name.GetString()!.Length>50||
    !row.TryGetProperty("fatorConversao",out var factor)||!factor.TryGetDouble(out var conversion)||!double.IsFinite(conversion))return Block("ACM_MATERIAL_RESPONSE_INVALID");
   if(code.GetString()!=codigo)return Block("ACM_MATERIAL_IDENTITY_MISMATCH");
   if(row.TryGetProperty("idAssinatura",out var signature)&&(!signature.TryGetInt32(out var returnedSignature)||returnedSignature!=idAssinatura))return Block("ACM_MATERIAL_SCOPE_MISMATCH");
   if(active!=1)return Block("ACM_MATERIAL_INACTIVE");
   return new(true,codigo,number,idAssinatura,status,"ACM_MATERIAL_CONFIRMED");
  }
  catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
  catch(GuardianHttpException error){status=error.HttpStatusCode;return Block(status==401?"ACM_MATERIAL_AUTHENTICATION_FAILED":status==403?"ACM_MATERIAL_AUTHORIZATION_FAILED":"ACM_MATERIAL_LOOKUP_UNVERIFIED");}
  catch(JsonException){return Block("ACM_MATERIAL_RESPONSE_INVALID");}
  catch(InvalidOperationException){return Block("ACM_MATERIAL_RESPONSE_INVALID");}
  catch{return Block("ACM_MATERIAL_LOOKUP_UNVERIFIED");}
 }
}

// Separate GET-only transport; existing SOAP, REST and token transports are unchanged.
public sealed class GuardianMaterialReadTransport:IDisposable
{
 public HttpClient Client{get;}=new(new ReadOnlyHandler(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false})){Timeout=Timeout.InfiniteTimeSpan};
 public void Dispose()=>Client.Dispose();
 sealed class ReadOnlyHandler(HttpMessageHandler inner):DelegatingHandler(inner)
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {if(request.Method!=HttpMethod.Get||request.RequestUri is not{Scheme:"https"} uri||uri.GetLeftPart(UriPartial.Authority)!="https://guardian.cloudprix.com.br"||!uri.AbsolutePath.StartsWith("/api/v0/Guardian/Item/",StringComparison.Ordinal))throw new InvalidOperationException("ACM_MATERIAL_GET_ONLY_ROUTE_REQUIRED");return base.SendAsync(request,ct);}
 }
}
