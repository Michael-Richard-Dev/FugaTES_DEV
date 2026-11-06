using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using FugaTES.Callback.Http;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.SAP.Http;
// O ambiente e explicito nos dois modos deste diagnostico, e sai no proprio JSON: um
// diagnostico que nao diz de qual SAP fala pode ser lido como atestado do outro.
// Uso: --verify-provider <diretorio> <DEV110|QAS120>   ou   <diretorio> <DEV110|QAS120>
if(args.Length==3&&args[0]=="--verify-provider"&&Path.IsPathFullyQualified(args[1])&&Ambiente(args[2]) is not null)
{
 if(!OperatingSystem.IsLinux())return 2;
 using var transport=new ToledoSapHttpTransport();
 var ambiente=Ambiente(args[2])!;
 var auth=new CarrierLinuxSapAuthentication(new Uri(ambiente.Origem),new CarrierSystemdCredentials(args[1]),"FUGATES");
 var proof=await FugaTES.Tools.SapProviderReadOnlyVerification.CheckAsync(transport.Client,auth,ambiente,default);
 Console.WriteLine(JsonSerializer.Serialize(new{diagnostic="SAP_PROVIDER_VERIFICATION",ambiente=ambiente.Nome,passed=proof.Passed,phase=proof.Phase,http_status=proof.HttpStatus,metadata_confirmed=proof.MetadataConfirmed,csrf_confirmed=proof.CsrfConfirmed,sap_posts=0}));
 return proof.Passed?0:1;
}
if(args.Length!=2||!Path.IsPathFullyQualified(args[0])||Ambiente(args[1]) is null)return 2;
AppDomain.CurrentDomain.FirstChanceException+=(_,e)=>{
 if(e.Exception is HttpRequestException h)Console.WriteLine(JsonSerializer.Serialize(new{diagnostic="HTTP_FAILURE_TYPE_ONLY",type=h.GetType().Name,category=h.HttpRequestError.ToString()}));
 else if(e.Exception is SocketException s)Console.WriteLine(JsonSerializer.Serialize(new{diagnostic="SOCKET_FAILURE_TYPE_ONLY",type=s.GetType().Name,category=s.SocketErrorCode.ToString()}));
 else if(e.Exception is CarrierServiceStartupException c&&System.Text.RegularExpressions.Regex.IsMatch(c.Message,@"\A[A-Z_]{1,128}\z"))Console.WriteLine(JsonSerializer.Serialize(new{diagnostic="PROVIDER_CODE_ONLY",code=c.Message}));
 else if(e.Exception is SapHttpStatusException status)Console.WriteLine(JsonSerializer.Serialize(new{diagnostic="CSRF_STATUS_ONLY",status=(int)status.StatusCode}));
};
using var observer=new HttpStatusObserver();
return await ToledoSapAuthenticationReadOnly.RunAsync(args[0],args[1]);

static FugaTES.Aplicacao.Configuracao.AmbienteSap? Ambiente(string? nome)
 => FugaTES.Aplicacao.Configuracao.AmbientesSap.Aprovados.FirstOrDefault(a=>string.Equals(a.Nome,nome?.Trim(),StringComparison.OrdinalIgnoreCase));
sealed class HttpStatusObserver:IObserver<DiagnosticListener>,IObserver<KeyValuePair<string,object?>>,IDisposable
{
 readonly List<IDisposable> subscriptions=[];
 public HttpStatusObserver()=>subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
 public void OnNext(DiagnosticListener listener){if(listener.Name=="HttpHandlerDiagnosticListener")subscriptions.Add(listener.Subscribe(this));}
 public void OnNext(KeyValuePair<string,object?> value)
 {
  if(!value.Key.EndsWith(".Stop",StringComparison.Ordinal)||value.Value is null)return;
  var type=value.Value.GetType();var req=type.GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage;
  var response=type.GetProperty("Response")?.GetValue(value.Value) as HttpResponseMessage;
  if(req?.RequestUri is not{} uri||!FugaTES.Aplicacao.Configuracao.AmbientesSap.EhOrigemAprovada(uri.GetLeftPart(UriPartial.Authority)))return;
  // Deliberately no Authorization, cookies, tokens, request/response body, query or exception message.
  Console.WriteLine(JsonSerializer.Serialize(new{diagnostic="READ_ONLY_HTTP_METADATA",method=req.Method.Method,path=uri.AbsolutePath,accept=req.Headers.Accept.Select(a=>a.MediaType).ToArray(),status=response is null?(int?)null:(int)response.StatusCode,contentType=response?.Content.Headers.ContentType?.MediaType}));
 }
 public void OnError(Exception error){}public void OnCompleted(){}
 public void Dispose(){foreach(var s in subscriptions)s.Dispose();}
}
