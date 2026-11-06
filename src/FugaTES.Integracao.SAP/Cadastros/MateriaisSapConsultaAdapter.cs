using FugaTES.Aplicacao.Consultas.CadastrosSap;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
namespace FugaTES.Integracao.SAP.Cadastros;
public sealed class MateriaisSapConsultaAdapter(HttpClient http,OpcoesSap opcoes,IAutenticacaoSap auth,TimeProvider? tempo=null)
 :SapCadastroConsultaBase(CategoriaCadastroSap.Materiais,http,opcoes,auth,tempo)
{
 const string Path="/sap/opu/odata/sap/API_PRODUCT_SRV/A_Product";
 protected override async Task<IReadOnlyList<CadastroSapLeitura>> LerCatalogoAsync(string code,CancellationToken ct)
 {
  var query="$select=Product,ProductType,BaseUnit,ProductGroup,IsMarkedForDeletion,CrossPlantStatus,to_Description&$expand=to_Description";
  if(code.Length>0)query+="&$filter="+Uri.EscapeDataString(FilterCode("Product",code,18));
  var all=await AllAsync(Path,"Product",query,ct).ConfigureAwait(false);var result=new List<CadastroSapLeitura>();
  foreach(var p in all)
  {
   var id=Text(p,"Product");if(code.Length>0&&!Matches(id,code))throw Error("SAP_IDENTITY_MISMATCH");
   var descriptions=Rows(p,"to_Description");
   foreach(var d in descriptions)if(Text(d,"Product") is{} linked&&linked.Length>0&&linked!=id)throw Error("SAP_IDENTITY_MISMATCH");
   var portuguese=descriptions.Where(d=>Text(d,"Language")=="PT").Select(d=>Text(d,"ProductDescription")).Distinct(StringComparer.Ordinal).ToArray();
   if(portuguese.Length>1)throw Error("SAP_DESCRIPTION_AMBIGUOUS");
   var name=portuguese.SingleOrDefault()??"";var deleted=Flag(p,"IsMarkedForDeletion");var status=deleted is true?"Marcado para exclusão":deleted is false?"Sem marcação de exclusão":"Exclusão não informada";
   var central=Text(p,"CrossPlantStatus");if(central.Length>0)status+="; status central: "+central;
   var info="Unidade: "+Text(p,"BaseUnit");if(name.Length==0)info+="; descrição PT não informada";
   result.Add(new(id,name,Text(p,"ProductType"),Text(p,"ProductGroup"),info,status));
  }
  return result;
 }
 public override async Task<CadastroSapDetalhe> ObterDetalheAsync(string codigo,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();if(!Key(codigo,40))throw Error("SAP_FILTER_INVALID");
  using var json=await GetAsync(Path+"('"+EncodedKey(codigo)+"')","$format=json&$expand=to_Description,to_Plant,to_ProductUnitsOfMeasure",ct).ConfigureAwait(false);var d=Data(json);
  if(Text(d,"Product")!=codigo)throw Error("SAP_IDENTITY_MISMATCH");
  return Detail(codigo,d,"to_Description","to_Plant","to_ProductUnitsOfMeasure");
 }
}
