using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Integracao.Guardian;
using FugaTES.Persistencia.ContratosSapToledo;
using FugaTES.Persistencia.ManualRetry;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Npgsql;

if(args.Length is not(3 or 5)||args[0] is not("--read-only" or "--reconcile")||args[1]!="--output"||!Path.IsPathFullyQualified(args[2])||args.Length==5&&(args[3]!="--read-code"||args[4] is not("1183" or "0000001183"))||!OperatingSystem.IsLinux()||System.Net.Dns.GetHostName()!="vm-fugatesbio")
{Console.WriteLine("{\"result\":\"BLOCKED_ARGUMENTS\"}");return 2;}
var id=Guid.Parse("4c8fd7ee-cb56-4605-90c3-13eb77d13327");var readCode=args.Length==5?args[4]:"0000001183";var reconcile=args[0]=="--reconcile";var operationId=Guid.Parse("8a4b5429-7b27-41ca-8660-73cdd364fcce");var mutationInvoked=false;var commitAcknowledged=false;
try
{
 if(File.Exists(args[2])||reconcile&&readCode!="1183")throw new InvalidOperationException("RECONCILIATION_OUTPUT_OR_NOMINAL_CODE_INVALID");
 var start=new ProcessStartInfo("/usr/bin/systemctl"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};foreach(var a in new[]{"show","fugates-acm-motor-dev.service","-p","MainPID","--value"})start.ArgumentList.Add(a);
 using var proc=Process.Start(start)!;var pid=(await proc.StandardOutput.ReadToEndAsync()).Trim();await proc.WaitForExitAsync();if(proc.ExitCode!=0||!int.TryParse(pid,out var n)||n<=0)throw new InvalidOperationException("MOTOR_NOT_RUNNING");
 var config=new ConfigurationBuilder().AddInMemoryCollection(Encoding.UTF8.GetString(File.ReadAllBytes("/proc/"+pid+"/environ")).Split('\0',StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Split('=',2)).Where(x=>x.Length==2).ToDictionary(x=>x[0].Replace("__",":"),x=>(string?)x[1])).Build();
 var normal=config.GetSection("AcmR57:Normal").Get<AcmR57NormalOptions>()??throw new InvalidOperationException();normal=normal with{AcmAutomatico=config.GetSection("MotorIntegracao:AcmAutomatico").Get<AcmAutomaticoOptions>()??new(),AcmInboundWindow=config.GetSection("MotorIntegracao:AcmInboundWindow").Get<AcmInboundWindowOptions>()??new()};
 if(!normal.DynamicEmitterValidationEnabled||normal.GuardianEnvironment!="HML"||normal.DynamicEmitterSignatureId!=133289)throw new InvalidOperationException("APPROVED_PROFILE_REQUIRED");
 var cs=new NpgsqlConnectionStringBuilder(config.GetConnectionString("FugaTES")){IncludeErrorDetail=false,Options="-c default_transaction_read_only=on",ApplicationName="FugaTES.ContractReconciliation.ReadOnly"};
 if(cs.Host!="127.0.0.1"||cs.Port!=5433||cs.Database!="fugates_dev"||cs.Username!="fugates_acm_hml_app"||!string.IsNullOrEmpty(cs.Password)||string.IsNullOrWhiteSpace(cs.Passfile))throw new InvalidOperationException("DATASOURCE_REJECTED");
 await using var source=NpgsqlDataSource.Create(cs.ConnectionString);using var bounded=new CancellationTokenSource(TimeSpan.FromSeconds(65));var ct=bounded.Token;
 await using(var c=await source.OpenConnectionAsync(ct))await AcmPostgreSqlIdentityGuard.VerifyAsync(c,ct);
 var version=await new ContratoSapToledoVersaoStorePostgreSql(source).ObterPorIntegracaoIdAsync(id,ct)??throw new InvalidOperationException("VERSION_MISSING");
 var snapshot=await new AcmManualRetryPostgreSql(source).ReadAsync(id,ct)??throw new InvalidOperationException("STATE_MISSING");
 if(version.NumeroContrato!="0000001183"||version.Versao!=1||!AcmManualRetryPolicy.IsKnownPreSendFailure(snapshot))throw new InvalidOperationException("EXACT_KNOWN_PRE_SEND_STATE_REQUIRED");
 var options=new GuardianHttpOptions{BaseUrl=new Uri(config["Guardian:BaseUrl"]!),Timeout=TimeSpan.FromSeconds(15),ContratoSoapEndpoint=new Uri(config["Guardian:ContratoSoapEndpoint"]!),ContratoSoapEnvironment=config["Guardian:ContratoSoapEnvironment"],ContratoSoapInternalHmlHttpApproved=bool.Parse(config["Guardian:ContratoSoapInternalHmlHttpApproved"]!)};
 using var auth=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(20)};
 var tokens=new GuardianTokenClient(auth,options,new GuardianRuntimeCredenciaisProvider(config));using var ep=new GuardianEmitterReadTransport();using var mp=new GuardianMaterialReadTransport();
 var emitter=new AcmGuardianEmitterResolver(normal,new GuardianEmitterReadAdapter(ep.Client,options,tokens,133289),TimeProvider.System);
 var material=new AcmGuardianMaterialResolver(normal,new GuardianMaterialReadAdapter(mp.Client,options,tokens,133289),TimeProvider.System);
 if(AcmGuardianMaterialResolver.UsesDynamic(normal,version))await material.ValidarAsync(version,ct);
 await emitter.ValidarAsync(version,ct);var preparation=new AcmR57NormalPreparation(normal,emitter,material);var desired=preparation.PrepararContrato(version);var sourcePrepared=desired;
 var originalSapCode=desired.Contrato.Codigo;
 // Explicit one-contract mapping confirmed by the operator; never a global normalization.
 if(readCode=="1183")desired=desired with{Contrato=desired.Contrato with{Codigo="1183"}};
 using var handler=new ConsultaOnlyHandler(readCode);using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(20)};
 var found=await new GuardianContratoSoapClient(http,options).ConsultarAsync(readCode,ct);
 var actual=found.Contrato;
 var fields=actual is null?new Dictionary<string,bool>():new Dictionary<string,bool>{["Codigo"]=actual.Codigo==desired.Contrato.Codigo,["Descricao"]=actual.Descricao==desired.Contrato.Descricao,["ValorContrato"]=actual.ValorContrato==desired.Contrato.ValorContrato,["Estado"]=actual.Estado==desired.Contrato.Estado,["ValorProximidadeFimContrato"]=actual.ValorProximidadeFimContrato==desired.Contrato.ValorProximidadeFimContrato,["TipoLancamento"]=actual.TipoLancamento==desired.Contrato.TipoLancamento,["CodigoEmissor"]=actual.CodigoEmissor==desired.Contrato.CodigoEmissor,["CodigoItem"]=actual.CodigoItem==desired.Contrato.CodigoItem};
 var identical=actual is not null&&SincronizarContratoGuardian.Identicos(actual,desired.Contrato);
 AcmExternalContractReconciliationResult? reconciliation=null;
 if(reconcile)
 {
  var proof=new AcmExternalContractReconciliationProof{OperationId=operationId,IntegrationId=id,SapCode=originalSapCode,ExternalCode=readCode,Version=version.Versao,Fingerprint=version.FingerprintSha256,SnapshotDigest=snapshot.Digest(),Lookup=found.Estado,Sequence=found.SequencialContrato??0,ObservedUtc=DateTimeOffset.UtcNow,SoapHttp=handler.Status,SoapResponseHash=handler.ResponseHash??"",Desired=desired,Observed=actual,
   OperatorSid="S-1-5-21-1842992099-521793823-1132869621-1001",AuthorizationReference="OPERATOR_REQUEST_RECONCILE1183_20261101",IdentifierMappingEvidence="OPERATOR_CONFIRMED_SAP0000001183_TO_GUARDIAN1183_20261101",BackupReference="DEV5433_BACKUP_C539444CBA1A0B39F9AA1CEC2AC8CEFA89A934972CC878CC3CDD5D71751E9AA9"};
  if(!AcmExternalContractReconciliationPolicy.CanReconcile(snapshot,proof,sourcePrepared,DateTimeOffset.UtcNow))throw new InvalidOperationException("RECONCILIATION_PREFLIGHT_BLOCKED");
  mutationInvoked=true;reconciliation=await new AcmManualRetryPostgreSql(source).ReconcileExternalContractAsync(proof,preparation,ct);commitAcknowledged=true;
 }
 var result=new{result=reconciliation?.Result??(found.Estado==EstadoConsultaContratoGuardian.Existente&&identical&&found.SequencialContrato>0?"MATCHED_EXISTING_CONTRACT":"BLOCKED_EXTERNAL_IDENTITY_OR_CONTENT"),reconciliation,sourceSapCode=originalSapCode,identifierMappingEvidence=readCode=="1183"?"OPERATOR_CONFIRMED_LITERAL1183":null,integrationId=id,contract=version.NumeroContrato,version=version.Versao,receivedUtc=version.RecebidoEmUtc,fingerprint=version.FingerprintSha256,snapshotDigest=snapshot.Digest(),lookup=found.Estado.ToString(),requestedGuardianCode=readCode,returnedGuardianCode=actual?.Codigo,sequence=found.SequencialContrato,expectedReference=desired.NumeroReferencia,fieldsMatch=fields,identical,expectedContractDigest=Hash(JsonSerializer.Serialize(desired.Contrato)),externalContractDigest=actual is null?null:Hash(JsonSerializer.Serialize(actual)),soapMetadata=handler.Diagnostics,soapResponseHash=handler.ResponseHash,soapHttp=handler.Status,soapReadCalls=handler.Calls,businessPosts=0,databaseMutationOutcome=commitAcknowledged?"CONFIRMED":"NOT_ATTEMPTED",atUtc=DateTimeOffset.UtcNow};
 using(var file=new FileStream(args[2],FileMode.CreateNew,FileAccess.Write,FileShare.None)){File.SetUnixFileMode(args[2],UnixFileMode.UserRead|UnixFileMode.UserWrite);await JsonSerializer.SerializeAsync(file,result,cancellationToken:ct);await file.FlushAsync(ct);}
 Console.WriteLine(JsonSerializer.Serialize(result));return result.result is "MATCHED_EXISTING_CONTRACT" or "RECONCILED_PROTOCOL_PENDING" or "ALREADY_RECONCILED"?0:1;
}
catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new{result=commitAcknowledged?"COMMITTED_EVIDENCE_WRITE_FAILED":mutationInvoked?"UNKNOWN_NO_RETRY":"BLOCKED_NO_MUTATION",operationId,exceptionType=ex.GetType().Name,code=ex is AcmR57BlockedException block?block.Code:null,sqlState=(ex as PostgresException)?.SqlState,businessPosts=0,databaseMutationOutcome=commitAcknowledged?"CONFIRMED":mutationInvoked?"UNKNOWN":"NOT_ATTEMPTED"}));return 1;}
static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
sealed class ConsultaOnlyHandler:DelegatingHandler
{
 public int Calls,Status;public string? ResponseHash;public object? Diagnostics;
 readonly string allowedCode;public ConsultaOnlyHandler(string code):base(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false}){if(code is not("1183" or "0000001183"))throw new InvalidOperationException();allowedCode=code;}
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  if(request.Method!=HttpMethod.Post||request.RequestUri?.AbsoluteUri!="http://192.168.17.91/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx"||request.Headers.GetValues("SOAPAction").SingleOrDefault()!="\"http://toledobrasil.com.br/WS_Guardian/ConsultaContrato\""||Calls!=0)throw new InvalidOperationException("ONLY_ONE_APPROVED_SOAP_READ_ALLOWED");
  using var input=XmlReader.Create(new StringReader(await request.Content!.ReadAsStringAsync(ct)),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});var xml=XDocument.Load(input);XNamespace soap="http://schemas.xmlsoap.org/soap/envelope/",ns="http://toledobrasil.com.br/WS_Guardian";
  var operation=xml.Root?.Element(soap+"Body")?.Elements().SingleOrDefault();if(operation?.Name!=ns+"ConsultaContrato"||operation.Descendants(ns+"CodigoContrato").SingleOrDefault()?.Value!=allowedCode)throw new InvalidOperationException("ONLY_1183_READ_ALLOWED");
  Calls++;var r=await base.SendAsync(request,ct);Status=(int)r.StatusCode;var bytes=await r.Content.ReadAsByteArrayAsync(ct);ResponseHash=Convert.ToHexString(SHA256.HashData(bytes));
  using var responseXml=XmlReader.Create(new MemoryStream(bytes),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576});var doc=XDocument.Load(responseXml);var entries=doc.Descendants().Where(e=>e.Name.LocalName=="VORetornoContrato").ToArray();var entry=entries.Length==1?entries[0]:null;var contracts=entry?.Elements().Where(e=>e.Name.LocalName=="Contrato").ToArray()??[];var contract=contracts.Length==1?contracts[0]:null;
  string? Scalar(XElement? parent,string name){var nodes=parent?.Elements().Where(e=>e.Name.LocalName==name).ToArray()??[];if(nodes.Length!=1)return null;var node=nodes[0];var text=node.HasElements?node.Elements().SingleOrDefault(e=>e.Name.LocalName=="Codigo")?.Value:node.Value;return text is{Length:>0 and <=40}&&text.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or ',' or '-' or '_')?text:null;}
  Diagnostics=new{entries=entries.Length,contracts=contracts.Length,error=Scalar(entry,"Erro"),responseNamespace=entry?.Name.NamespaceName,contractFields=contract?.Elements().Select(e=>e.Name.LocalName).ToArray(),values=new[]{"Codigo","Sequencial","Estado","TipoLancamento","ValorContrato","ValorProximidadeFimContrato","CodigoEmissor","CodigoItem"}.ToDictionary(n=>n,n=>Scalar(contract,n)),descriptionPresent=contract?.Elements().Any(e=>e.Name.LocalName=="Descricao"&&!string.IsNullOrWhiteSpace(e.Value)),fault=doc.Descendants(soap+"Fault").Any()};return r;
 }
}
