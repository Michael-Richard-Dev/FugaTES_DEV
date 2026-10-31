using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Integracao.Guardian;
namespace FugaTES.WinForms.Acm;

public sealed record VinculosRestAcmR55(string NumeroContratoSap,long VersaoH4,string CodigoItemGuardian,
 string CodigoEmissorGuardian,string CodigoTransportadoraGuardian,string CallbackOperacional,string FonteComprovacao);
public sealed record AcmDualPreviewR55(IReadOnlyList<AcmPreviewFieldR53> SapCampos,IReadOnlyList<AcmPreviewFieldR53> SoapCampos,
 IReadOnlyList<AcmPreviewFieldR53> RestCampos,string SoapXml,string RestJson,bool SoapMapeado,bool RestMapeado,string SapEstado)
{
 public bool EnvioSoapPermitido=>false;
 public bool EnvioRestPermitido=>false;
 // This preview cannot invoke a writer, even with a complete offline mapping. Real journal/idempotency/runtime are not composed.
 public Task EnviarSoapAsync(IGuardianContratoSincronizacaoGateway gateway,CancellationToken ct=default)=>Task.FromException(new InvalidOperationException("R55_SOAP_WRITE_DISABLED"));
 public Task EnviarRestAsync(IPreCadastroGuardianSet26Gateway gateway,CancellationToken ct=default)=>Task.FromException(new InvalidOperationException("R55_REST_WRITE_DISABLED"));
 public static AcmDualPreviewR55 Criar(VersaoContratoSapToledo? origem=null,string? evidenciaSap=null,
  DecisaoContratoAcmGuardianComprovada? decisao=null,VinculosRestAcmR55? vinculos=null,string? transportadoraCandidata=null)
 {
  const string pending=AcmRestPreviewR53.Pending;const string doc="R52/REV1 documental; não consulta SAP nesta sessão";
  var valid=origem is not null&&origem.IntegracaoId!=Guid.Empty&&origem.NumeroContrato==origem.Snapshot.NumeroContrato&&
   (origem.NumeroContrato=="0000001031"||origem.NumeroContrato=="1031")&&origem.Versao==2&&origem.Snapshot.Centro=="3011"&&!Pendente(evidenciaSap);
  var source=valid?origem!.Snapshot:null;var origin=valid?evidenciaSap!:doc;
  var sap=new List<AcmPreviewFieldR53>{new("Contrato SAP",source?.NumeroContrato??"0000001031",valid?"FORNECIDO COM REFERÊNCIA":"ALVO DOCUMENTAL",origin),
   new("Versão interna","2",valid?"VERSÃO DA FONTE":"ALVO DOCUMENTAL — NÃO VAI AO JSON",origin),
   new("Centro",source?.Centro??"3011",valid?"FORNECIDO COM REFERÊNCIA":"ALVO DOCUMENTAL",origin),
   new("Descrição contrato",source?.DescricaoTipoContrato??pending,source?.DescricaoTipoContrato is null?"PENDENTE":"FONTE SAP",origin),
   new("Quantidade/peso",source?.QuantidadeContrato?.ToString(CultureInfo.InvariantCulture)??pending,source?.QuantidadeContrato is null?"PENDENTE — NÃO INVENTAR":"FONTE SAP → ValorContrato",origin),
   new("ValorUnitario",source?.ValorUnitario?.ToString(CultureInfo.InvariantCulture)??"NÃO DISPONÍVEL","NÃO USADO COMO ValorContrato",origin),
   new("Material SAP",source?.Material??"000000000007000000",valid?"IDENTIFICADOR DA FONTE; CORRESPONDÊNCIA GUARDIAN PENDENTE":"BASELINE DOCUMENTAL",origin),
   new("Emissor SAP",source?.IdParceiro??"0020000184",valid?"IDENTIFICADOR DA FONTE; CORRESPONDÊNCIA GUARDIAN PENDENTE":"BASELINE DOCUMENTAL",origin)};
  var scoped=valid&&decisao is not null&&decisao.NumeroContratoSap==origem!.NumeroContrato&&decisao.VersaoH4==origem.Versao&&!Pendente(decisao.FonteAprovacao)&&!Pendente(decisao.CodigoGuardian);
  ContratoGuardianSet26? mapped=null;
  if(scoped)try{mapped=new MapeadorContratoAcmGuardian(new(null,"HML")).MapearComDecisaoComprovada(origem!,decisao!);}catch(FugaTES.Aplicacao.Servicos.SAP.DecisaoFuncionalPendenteSapException){ }
  var code=scoped?decisao!.CodigoGuardian:pending;
  var values=new Dictionary<string,string?>{["Codigo"]=scoped?code:null,["Descricao"]=!Pendente(source?.DescricaoTipoContrato)?source!.DescricaoTipoContrato:null,
   ["ValorContrato"]=source?.QuantidadeContrato is >=0?source.QuantidadeContrato.Value.ToString(CultureInfo.InvariantCulture):null,
   ["Estado"]=scoped&&!Pendente(decisao!.Estado)?decisao.Estado:null,["TipoLancamento"]=scoped&&!Pendente(decisao!.TipoLancamento)?decisao.TipoLancamento:null,
   ["ValorProximidadeFimContrato"]="0"};
  var soap=values.Select(v=>new AcmPreviewFieldR53(v.Key,v.Value??pending,v.Value is null?"PENDENTE — NÃO ENVIÁVEL":v.Key=="ValorProximidadeFimContrato"?"REGRA TEMPORÁRIA HML 3011; NÃO DiasToler":"DADO/FUNÇÃO COM REFERÊNCIA",v.Key is "Codigo" or "Estado" or "TipoLancamento"?scoped?decisao!.FonteAprovacao:"DECISÃO FUNCIONAL AUSENTE":origin)).ToList();
  var xml=(mapped is not null?GuardianContratoSoapCodec.CriarCadastro(mapped):GuardianContratoSoapCodec.CriarRascunho(values)).ToString();
  var linksValid=valid&&scoped&&vinculos is not null&&vinculos.NumeroContratoSap==origem!.NumeroContrato&&vinculos.VersaoH4==origem.Versao&&
   !new[]{vinculos.CodigoItemGuardian,vinculos.CodigoEmissorGuardian,vinculos.CodigoTransportadoraGuardian,vinculos.CallbackOperacional,vinculos.FonteComprovacao}.Any(Pendente)&&
   code.Length<=20&&vinculos.CodigoItemGuardian.Length<=30&&vinculos.CodigoEmissorGuardian.Length<=20&&vinculos.CodigoTransportadoraGuardian.Length<=20&&
   Uri.TryCreate(vinculos.CallbackOperacional,UriKind.Absolute,out var callback)&&callback.Scheme=="https"&&callback.UserInfo.Length==0;
  var candidate=!Pendente(transportadoraCandidata)&&transportadoraCandidata!.Length<=20?transportadoraCandidata:pending;
  var restData=new PreCadastroRecebimentoGuardianSet26(0,"FLX_RECEBIMENTO",linksValid?vinculos!.CodigoItemGuardian:pending,
   linksValid?vinculos!.CodigoEmissorGuardian:pending,linksValid?vinculos!.CodigoTransportadoraGuardian:candidate,code.Length<=20?code:pending,1,"OC1031",
   linksValid?vinculos!.CallbackOperacional:pending,[new(1000,source?.TipoContrato??"AC3P"),new(1002,source?.Centro??"3011"),new(1003,source?.OperacaoContrato??"10")]);
  var rest=new List<AcmPreviewFieldR53>{new("codigoContrato",restData.CodigoContrato,scoped&&code.Length<=20?"REPRESENTAÇÃO APROVADA; NÃO PROVA CONTRATO SINCRONIZADO":"PENDENTE — NÃO USAR null DO EXEMPLO",scoped?decisao!.FonteAprovacao:doc),
   new("codigoFluxo","FLX_RECEBIMENTO","REGRA DOCUMENTAL ACM ENTRADA",doc),new("codigoItem",restData.CodigoItem,linksValid?"CORRESPONDÊNCIA COM REFERÊNCIA":"PENDENTE",linksValid?vinculos!.FonteComprovacao:doc),
   new("codigoEmissor",restData.CodigoEmissor,linksValid?"CORRESPONDÊNCIA COM REFERÊNCIA":"PENDENTE",linksValid?vinculos!.FonteComprovacao:doc),
   new("codigoTransportadora",restData.CodigoTransportadora!,linksValid?"CORRESPONDÊNCIA/APROVAÇÃO COM REFERÊNCIA":candidate==pending?"PENDENTE — CANDIDATA NÃO CONFIGURADA":"CANDIDATA CONFIGURADA; CADASTRO/APROVAÇÃO PENDENTES",linksValid?vinculos!.FonteComprovacao:"PARÂMETRO PÚBLICO DA DEMONSTRAÇÃO"),
   new("urlParaRetorno",restData.UrlParaRetorno,linksValid?"CALLBACK OPERACIONAL COM REFERÊNCIA":"PENDENTE — WEBHOOK EXTERNO NÃO ADOTADO",linksValid?vinculos!.FonteComprovacao:doc),
   new("numeroReferencia","OC1031","DOCUMENTAL — NÃO É IDEMPOTÊNCIA",doc),new("idAgenda","1","AGENDA DOCUMENTAL",doc),
   new("adicionais1000/1002/1003",string.Join(" / ",restData.CamposAdicionais.Select(c=>c.Valor)),valid?"CONTEXTO DA FONTE SAP":"CONTEXTO DOCUMENTAL",origin),
   new("placas/motorista/documentos","OMITIDOS","OPCIONAIS SEM DADOS COMPROVADOS; NÃO COPIAR EXEMPLO",doc)};
  var resolver=new DefaultJsonTypeInfoResolver();resolver.Modifiers.Add(t=>{if(t.Type==typeof(PreCadastroRecebimentoGuardianSet26))foreach(var property in t.Properties)if(property.Name.Equals("documentos",StringComparison.OrdinalIgnoreCase))property.ShouldSerialize=(_,value)=>value is System.Collections.ICollection c&&c.Count>0;});
  var json=JsonSerializer.Serialize(new GuardianPreCadastroRequestDto(new(restData.UrlParaRetorno),restData),new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping,TypeInfoResolver=resolver});
  var restMapped=linksValid&&source?.TipoContrato=="AC3P"&&source.OperacaoContrato=="10";
  sap.AddRange([new("SOAP ConsultaContrato","NÃO EXECUTADA","LEITURA REQUER GATE PRÓPRIO",doc),new("SOAP CadastraContrato",mapped is null?"MAPEAMENTO PARCIAL":"MAPEADO OFFLINE","ENVIO BLOQUEADO; JOURNAL/IDEMPOTÊNCIA/RUNTIME NECESSÁRIOS",doc),
   new("SOAP AlteraContrato","NÃO EXECUTADA","ENVIO BLOQUEADO; COMPARAÇÃO SAP COMO FONTE MESTRE",doc),new("REST Pré-cadastro",restMapped?"DADOS MAPEADOS OFFLINE":"VÍNCULOS PENDENTES","ENVIO BLOQUEADO; NÃO É CADASTRO MESTRE",doc)]);
  return new(sap,soap,rest,xml,json,mapped is not null,restMapped,valid?"SNAPSHOT FORNECIDO COM REFERÊNCIA; NÃO É HTTP DESTA SESSÃO":origem is null?"NÃO CONSULTADO — APENAS BASELINE DOCUMENTAL":"BLOQUEADO — IDENTIDADE/VERSÃO/ORIGEM NÃO COMPROVADA");
 }
 static bool Pendente(string? v)=>string.IsNullOrWhiteSpace(v)||new[]{"NÃO VALIDADO","NAO VALIDADO","PENDENTE"}.Contains(v.Trim(),StringComparer.OrdinalIgnoreCase);
}
