using System.Text;
using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Integracao.SAP.Http;

// One controlled delivery, with a read-before-write check. Never retries a POST.
public sealed class DocumentoToledoSapGateway(SapODataHttpExecutor executor,ToledoSapPayloadOptions? payloadOptions=null) : IDocumentoToledoSapGateway
{
    public const string ResourcePath="/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem";
    public async Task<RetornoToledoSap> EnviarOriginalAsync(DocumentoToledoSap document,Func<PlanoEnvioToledoSap,CancellationToken,Task<bool>> autorizarPost,CancellationToken ct)
    {
        if(!document.Valido())return new(ResultadoToledoSapCodigo.PreEnvioBloqueado,null,false);
        ToledoSapPreparedPayload prepared;Uri uri;string csrf;
        try
        {
            prepared=ToledoSapODataPayload.Prepare(document,payloadOptions??new());
            uri=executor.ConstruirUri(ResourcePath);
            if(uri.Scheme!="https"||uri.UserInfo.Length!=0)return new(ResultadoToledoSapCodigo.PreEnvioBloqueado,null,false);
            // Check canonical SAP ID, original Guardian ID and unpadded legacy alias before any POST.
            // Cover every zero-width alias up to10 digits; a legacy match requires reconciliation, never another creation.
            var numeric=prepared.IdPesagem.TrimStart('0');if(numeric.Length==0)numeric="0";
            var zeroAliases=Enumerable.Range(numeric.Length,11-numeric.Length).Select(width=>numeric.PadLeft(width,'0'));
            foreach(var id in new[]{prepared.IdPesagem,document.IdPesagem,numeric}.Concat(zeroAliases).Distinct(StringComparer.Ordinal))
            {
                var check=executor.ConstruirUri(ResourcePath+$"(IdPesagem='{id}',SistemaBalanca='Toledo',Centro='{prepared.Centro}')","$expand=to_Pesagem,to_QualidPes");
                var existing=await executor.ObterAtomAsync(check,ct);
                if((int)existing.Status==200)
                {
                    var proof=id==prepared.IdPesagem?ToledoSapAtomReceipt.Validate(existing.Body,prepared,uri):null;
                    return proof is not null?Confirmed(ResultadoToledoSapCodigo.RecebimentoJaConfirmadoSap,200,false,proof,prepared):
                        new(ResultadoToledoSapCodigo.RegistroSapExistenteDivergente,200,false){DetailCode="SAP_EXISTING_ENTITY_REQUIRES_RECONCILIATION",RequestSha256=prepared.Sha256,PayloadVersion=ToledoSapPreparedPayload.Version};
                }
                // An HTML/proxy 404, failed lookup or malformed response is NOT absence proof.
                if((int)existing.Status!=404)
                    return new(ResultadoToledoSapCodigo.FalhaAntesDoPost,(int)existing.Status,false){DetailCode="SAP_EXISTENCE_NOT_VERIFIED"};
                var filter=Uri.EscapeDataString($"IdPesagem eq '{id}' and SistemaBalanca eq 'Toledo' and Centro eq '{prepared.Centro}'");
                var collection=await executor.ObterAtomAsync(executor.ConstruirUri(ResourcePath,$"$filter={filter}&$inlinecount=allpages&$top=2"),ct);
                if((int)collection.Status!=200||!ToledoSapAtomReceipt.ProvesEmptyCollection(collection.Body,uri))
                    return new(ResultadoToledoSapCodigo.FalhaAntesDoPost,(int)collection.Status,false){DetailCode="SAP_EXISTENCE_NOT_VERIFIED"};
            }
            csrf=await executor.ObterTokenCsrfAsync(executor.ConstruirUri("/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/"),ct);
        }
        catch(ToledoDeliveryPartnerException e){return new(ResultadoToledoSapCodigo.PreEnvioBloqueado,null,false){DetailCode=e.Code};}
        catch(ToledoSapContractPartnerException e){return new(ResultadoToledoSapCodigo.PreEnvioBloqueado,null,false){DetailCode=e.Code};}
        catch(ToledoSapPayloadException e){return new(ResultadoToledoSapCodigo.PreEnvioBloqueado,null,false){DetailCode=e.Code};}
        catch(SapHttpStatusException e){return new(ResultadoToledoSapCodigo.FalhaAntesDoPost,(int)e.StatusCode,false);}
        catch{return new(ResultadoToledoSapCodigo.FalhaAntesDoPost,null,false);}
        try
        {
            ct.ThrowIfCancellationRequested();
            if(!await autorizarPost(new(prepared.Sha256,ToledoSapPreparedPayload.Version,prepared.EpochMilliseconds){DeliveryPartners=prepared.DeliveryPartners,ParceirosVersao=ToledoSapContractPartners.Version,ParceirosContrato=prepared.ParceirosContrato},ct))return new(ResultadoToledoSapCodigo.FalhaAntesDoPost,null,false);
            ct.ThrowIfCancellationRequested();
            using var content=new StringContent(prepared.Json,Encoding.UTF8,"application/json");
            var response=await executor.EnviarAtomComCsrfAsync(HttpMethod.Post,uri,content,csrf,null,ct);
            var code=(int)response.Status;
            var proof=code==201?ToledoSapAtomReceipt.Validate(response.Body,prepared,uri):null;
            if(proof is not null)return Confirmed(ResultadoToledoSapCodigo.RecebimentoConfirmadoSap,code,true,proof,prepared);
            var result=code is >=200 and <300?ResultadoToledoSapCodigo.AceiteHttpSemConfirmacaoEmpresarial:
                code is >=400 and <500&&code is not(408 or 429)?ResultadoToledoSapCodigo.RejeicaoHttp:ResultadoToledoSapCodigo.ResultadoIndeterminado;
            return new(result,code,true){RequestSha256=prepared.Sha256,PayloadVersion=ToledoSapPreparedPayload.Version};
        }
        catch{return new(ResultadoToledoSapCodigo.ResultadoIndeterminado,null,true){RequestSha256=prepared.Sha256,PayloadVersion=ToledoSapPreparedPayload.Version};}
    }
    static RetornoToledoSap Confirmed(ResultadoToledoSapCodigo code,int status,bool sent,ToledoSapAtomConfirmation proof,ToledoSapPreparedPayload prepared)
        =>new(code,status,sent){EntityId=proof.EntityId,RequestSha256=prepared.Sha256,ResponseSha256=proof.ResponseSha256,PayloadVersion=ToledoSapPreparedPayload.Version};
}
