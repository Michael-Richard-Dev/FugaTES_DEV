using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
namespace FugaTES.Infraestrutura.Mm;
/// <summary>Allowlisted observation documents. No arbitrary JSON/message crosses the read boundary.</summary>
public static class MmSnapshotDocumentGuard
{
 private static readonly JsonSerializerOptions Strict=new(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 private static readonly string[] States=["MM_NOT_STARTED","MM_SAP_COMPLETE","MM_COLLECTING","MM_SAP_AUTHENTICATION_REQUIRED","MM_SAP_AUTHORIZATION_REQUIRED","MM_SAP_QUERY_REJECTED","MM_SAP_TIMEOUT","MM_SAP_UNAVAILABLE","MM_SAP_RESPONSE_INVALID","MM_SAP_PROVIDER_UNAVAILABLE","MM_COLLECTOR_UNAVAILABLE","MM_VM_STOPPED"];
 private static readonly string[] GuardianStates=["MM_GUARDIAN_DESATIVADO","MM_GUARDIAN_PRONTO","MM_GUARDIAN_VERIFICADO","MM_GUARDIAN_COM_BLOQUEIOS","MM_GUARDIAN_BLOQUEADO_DATA","MM_GUARDIAN_BLOQUEADO_LEASE","MM_GUARDIAN_INDISPONIVEL"];
 public static void Validate(MmPurchaseOrderSnapshot value)
 {
  var now=DateTimeOffset.UtcNow;
  if(value.Host!="vm-fugatesbio"||value.IntervalSeconds!=180||!States.Contains(value.State)||!GuardianStates.Contains(value.GuardianState))Reject();
  if(value.LastAttemptUtc>now.AddSeconds(5)||value.LastSuccessUtc>now.AddSeconds(5)||value.RegistryReadUtc>now.AddSeconds(5)||value.NextPollUtc>now.AddSeconds(275))Reject();
  if(value.State=="MM_SAP_COMPLETE"&&(value.LastSuccessUtc is null||(value.LastAttemptUtc is {} a&&value.LastSuccessUtc<a)))Reject();
  if(value.GuardianRecords.Count>0&&(value.RegistryReadUtc is null||value.GuardianCallback is null))Reject();
  if(value.GuardianCallback is not null)ValidateCallback(value.GuardianCallback);
  foreach(var row in value.Items)ValidateSource(row);
  var keys=new HashSet<string>(StringComparer.Ordinal);var ids=new HashSet<Guid>();var protocols=new HashSet<long>();
  foreach(var row in value.GuardianRecords)
  {
   if(row is null||row.ReservaId==Guid.Empty||!ids.Add(row.ReservaId)||!keys.Add(row.Chave)||!Digits(row.Pedido,10)||!Digits(row.Item,5)||row.Centro is not("3011" or "3008")||row.Chave!="3000/"+row.Pedido+"/"+row.Item||row.AtualizadoUtc>now.AddSeconds(5)||row.AtualizadoUtc>value.RegistryReadUtc?.AddSeconds(5)||row.Http is <100 or >599||!Hash(row.Sha256)||!Hash(row.SourceSha256)||row.JsonEnviado is null||row.JsonEnviado.Length>65536||row.Mensagem?.Length>400)Reject();
   if(row.Estado is not(EstadoProtocoloMm.Criado or EstadoProtocoloMm.Recusado or EstadoProtocoloMm.Indeterminado))Reject();
   if(row.Estado==EstadoProtocoloMm.Criado&&(row.Http is not(>=200 and <300)||row.ProtocoloId is not >0))Reject();
   if(row.Estado!=EstadoProtocoloMm.Criado&&row.ProtocoloId is not null)Reject();
   if(row.ProtocoloId is {} protocol&&!protocols.Add(protocol))Reject();
   if(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.JsonEnviado)))!=row.Sha256)Reject();
   ValidateGuardianDocument(row,value.GuardianCallback!);
  }
 }
 private static void ValidateSource(PedidoCompraMmLeitura row)
 {
  if(!MmCollectionPolicy.Eligible(row)||!Digits(row.Pedido,10)||!Digits(row.Item,5)||row.Quantidade<0||!Code(row.TipoPedido,4)||!Code(row.Material,40,true)||!Code(row.GrupoMaterial,9,true)||!Code(row.Fornecedor,20)||!Code(row.Unidade,3)||row.Json is null||row.Json.Length>65536)Reject();
  var expected=JsonSerializer.SerializeToNode(new{CreationDate=row.DataCriacao!.Value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),PurchaseOrder=row.Pedido,PurchaseOrderType=row.TipoPedido,CompanyCode=row.Empresa,Supplier=row.Fornecedor,PurchasingProcessingStatus=row.EstadoSap,
   _PurchaseOrderItem=new{PurchaseOrder=row.Pedido,PurchaseOrderItem=row.Item,Plant=row.Centro,Material=row.Material,MaterialGroup=row.GrupoMaterial,OrderQuantity=row.Quantidade,PurchaseOrderQuantityUnit=row.Unidade}});
  if(!JsonNode.DeepEquals(expected,JsonNode.Parse(row.Json,new JsonNodeOptions(),new JsonDocumentOptions{MaxDepth=8})))Reject();
 }
 private static void ValidateGuardianDocument(RegistroProtocoloMm row,string callback)
 {
  using var document=JsonDocument.Parse(row.JsonEnviado,new JsonDocumentOptions{MaxDepth=8});NoDuplicates(document.RootElement);
  var dto=JsonSerializer.Deserialize<ProtocoloPreCadastroMmRequest>(row.JsonEnviado,Strict);
  if(dto?.Configuracao?.UrlParaRetorno!=callback||dto.DadosProtocoloPreCadastro is not {} data||data.Id!=0||!Code(data.CodigoItem,30)||!Code(data.CodigoEmissor,20)||data.CodigoItem=="0"||data.CodigoEmissor=="0")Reject();
  var fields=dto!.DadosProtocoloPreCadastro.CamposAdicionais;
  string[] expected=["E",row.Centro,"0","0","0",row.Pedido,"0","0","0",row.Item];
  if(fields is null||fields.Count!=10||fields.Any(x=>x is null)||fields.Select(x=>x.Numero).Distinct().Count()!=10)Reject();
  foreach(var field in fields)if(field.Numero is <1000 or >1009||field.Valor!=expected[field.Numero-1000])Reject();
 }
 private static void NoDuplicates(JsonElement value)
 {
  if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in value.EnumerateObject()){if(!names.Add(p.Name))Reject();NoDuplicates(p.Value);}}
  else if(value.ValueKind==JsonValueKind.Array)foreach(var x in value.EnumerateArray())NoDuplicates(x);
 }
 private static bool Hash(string? value)=>value is not null&&Regex.IsMatch(value,"^[0-9A-F]{64}$",RegexOptions.CultureInvariant);
 private static bool Digits(string? value,int max)=>value is not null&&value.Length>0&&value.Length<=max&&value.All(char.IsAsciiDigit)&&value.Any(x=>x!='0');
 private static bool Code(string? value,int max,bool allowEmpty=false)=>value is not null&&value.Length<=max&&(allowEmpty&&value.Length==0||value.Length>0&&Regex.IsMatch(value,"^[A-Za-z0-9._-]+$",RegexOptions.CultureInvariant));
 private static void ValidateCallback(string value)
 {if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)Reject();}
 [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Reject()=>throw new InvalidOperationException("MM_VM_OBSERVATION_DOCUMENT_REJECTED");
 private static string RejectionSummary(RegistroProtocoloMm row)
 {
  var safe=$"Guardian recusou a requisição (HTTP {row.Http}); conferir dados/permissão.";
  var match=Regex.Match(row.Mensagem??"",@"^Guardian recusou a requisição \(HTTP ([0-9]{3})\)\. Código: (-?[0-9]{1,9}|(?:E_|GUARDIAN_)[A-Z0-9_]{1,40})\.",RegexOptions.CultureInvariant);
  if(match.Success&&int.TryParse(match.Groups[1].Value,out var status)&&status==row.Http)return $"Guardian recusou a requisição (HTTP {row.Http}). Código: {match.Groups[2].Value}.";
  return safe;
 }
 public static MmPurchaseOrderSnapshot ForRead(MmPurchaseOrderSnapshot value)
 {
  Validate(value);
  return value with{GuardianRecords=value.GuardianRecords.Select(row=>row with{Mensagem=row.Estado switch
   {EstadoProtocoloMm.Criado=>"Protocolo Guardian confirmado.",EstadoProtocoloMm.Recusado when FugaTES.Aplicacao.Servicos.Mm.MmRetryPolicy.IsPrecheck(row.Mensagem)=>row.Mensagem,
    EstadoProtocoloMm.Recusado=>RejectionSummary(row),_=>"Registro sem resultado externo comprovado; não reenviar automaticamente."}}).ToArray()};
 }
}
