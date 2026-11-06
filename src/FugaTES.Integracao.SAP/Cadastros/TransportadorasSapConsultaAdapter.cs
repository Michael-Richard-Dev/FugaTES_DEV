using FugaTES.Aplicacao.Consultas.CadastrosSap;
using FugaTES.Aplicacao.Transportadoras.SnapshotsR24;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Mapeamentos;
namespace FugaTES.Integracao.SAP.Cadastros;
public sealed class TransportadorasSapConsultaAdapter(HttpClient http,OpcoesSap opcoes,IAutenticacaoSap auth,TimeProvider? tempo=null)
 :SapCadastroConsultaBase(CategoriaCadastroSap.Transportadoras,http,opcoes,auth,tempo)
{
 const string Path="/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner";
 const string Expand="to_Customer,to_Supplier,to_BusinessPartnerRole,to_BusinessPartnerTax,to_BusinessPartnerAddress,to_BusinessPartnerAddress/to_AddressUsage,to_BusinessPartnerAddress/to_PhoneNumber,to_BusinessPartnerAddress/to_EmailAddress";
 protected override async Task<IReadOnlyList<CadastroSapLeitura>> LerCatalogoAsync(string code,CancellationToken ct)
 {
  var filter="BusinessPartnerRole eq 'CRM010'";if(code.Length>0)filter+=" and "+FilterCode("BusinessPartner",code,10);
  var roles=await AllAsync(Path+"Role","BusinessPartner","$select=BusinessPartner,BusinessPartnerRole,ValidFrom,ValidTo&$filter="+Uri.EscapeDataString(filter),ct).ConfigureAwait(false);
  if(roles.Any(x=>Text(x,"BusinessPartnerRole")!="CRM010"))throw Error("SAP_CRM010_NOT_CONFIRMED");
  var expected=roles.ToDictionary(x=>Text(x,"BusinessPartner"),StringComparer.Ordinal);
  if(code.Length>0&&expected.Keys.Any(x=>!Matches(x,code)))throw Error("SAP_IDENTITY_MISMATCH");
  var result=new List<CadastroSapLeitura>();var reference=Now;
  foreach(var chunk in expected.Keys.Chunk(20))
  {
   var ids=chunk.ToHashSet(StringComparer.Ordinal);var condition=string.Join(" or ",chunk.Select(id=>"BusinessPartner eq "+Literal(id)));
   var bps=await AllAsync(Path,"BusinessPartner","$filter="+Uri.EscapeDataString("("+condition+")")+"&$select=BusinessPartner,BusinessPartnerFullName,BusinessPartnerCategory,BusinessPartnerGrouping,BusinessPartnerIsBlocked",ct).ConfigureAwait(false);
   foreach(var bp in bps)
   {
    var id=Text(bp,"BusinessPartner");if(!ids.Remove(id))throw Error("SAP_IDENTITY_MISMATCH");var role=expected[id];var blocked=Flag(bp,"BusinessPartnerIsBlocked");
    var status=(blocked is true?"BP bloqueado":blocked is false?"Sem bloqueio geral":"Bloqueio não informado")+"; "+Vigencia(role,reference);
    var kind=Text(bp,"BusinessPartnerCategory");var person=kind switch{"1"=>"PF","2"=>"PJ",_=>"Categoria SAP "+kind};
    result.Add(new(id,Text(bp,"BusinessPartnerFullName"),person,Text(bp,"BusinessPartnerGrouping"),"Papel SAP CRM010",status));
   }
   if(ids.Count!=0)throw Error("SAP_JOIN_INCOMPLETE");
  }
  return result;
 }
 public override async Task<CadastroSapDetalhe> ObterDetalheAsync(string codigo,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();if(!Key(codigo,10))throw Error("SAP_FILTER_INVALID");
  using var json=await GetAsync(Path+"('"+EncodedKey(codigo)+"')","$format=json&$expand="+Uri.EscapeDataString(Expand),ct).ConfigureAwait(false);var d=Data(json);
  if(Text(d,"BusinessPartner")!=codigo)throw Error("SAP_IDENTITY_MISMATCH");
  var role=Rows(d,"to_BusinessPartnerRole").Where(x=>Text(x,"BusinessPartnerRole")=="CRM010").ToArray();
  if(role.Length==0||role.Any(x=>Text(x,"BusinessPartner") is{} linked&&linked.Length>0&&linked!=codigo))throw Error("SAP_CRM010_NOT_CONFIRMED");
  var detail=Detail(codigo,d,"to_BusinessPartnerRole","to_BusinessPartnerTax","to_BusinessPartnerAddress");
  foreach(var address in Rows(d,"to_BusinessPartnerAddress")){Rows(address,"to_AddressUsage");Rows(address,"to_PhoneNumber");Rows(address,"to_EmailAddress");}
  return detail;
 }
 static string Vigencia(System.Text.Json.JsonElement role,DateTimeOffset reference)
 {
  if(!ParserDataODataV2.TentarParse(Text(role,"ValidFrom"),out var from)||!ParserDataODataV2.TentarParse(Text(role,"ValidTo"),out var to))return "CRM010 — vigência não confirmada";
  var state=new Crm010BusinessDatePolicyR25T().Evaluate(from,to,reference).Status;
  return state switch{AvaliacaoCrm010R24.EVALUATED_VALID=>"CRM010 vigente",AvaliacaoCrm010R24.EVALUATED_INVALID=>"CRM010 fora de vigência",_=>"CRM010 — vigência não confirmada"};
 }
}
