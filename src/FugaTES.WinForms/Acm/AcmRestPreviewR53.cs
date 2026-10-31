using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Encodings.Web;
using FugaTES.Integracao.Guardian;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Recebimento;
namespace FugaTES.WinForms.Acm;

public sealed record AcmPreviewFieldR53(string Campo, string Valor, string Situacao, string Origem);
public sealed record AcmRestPreviewR53(IReadOnlyList<AcmPreviewFieldR53> Campos, string RascunhoJson, string SapStatus)
{
    public const string Pending = "NÃO VALIDADO";
    public bool ProntoParaEmissao => false;
    // Even valid-looking markers/DTOs are preview-only: no authorization, journal or write gateway is composed.
    public Task<ProtocoloGuardianSet26> EnviarAsync(IPreCadastroGuardianSet26Gateway gateway, CancellationToken ct=default)
        => Task.FromException<ProtocoloGuardianSet26>(new InvalidOperationException("R53_POST_NOT_AUTHORIZED_PREVIEW_ONLY"));

    public static AcmRestPreviewR53 CriarParcial(string? codigoTransportadoraPadrao, string? urlParaRetorno,
        ContratoSapToledoSnapshot? sap = null, int? versao = null, string? evidencia = null)
    {
        var baseline = Criar(sap, versao, evidencia);
        const string origem = "Corpo/retorno HML informado pelo usuário; não executado por esta sessão";
        // Only presentation context survives from the complete documentary model.
        var campos = baseline.Campos.Where(c => !c.Campo.StartsWith("dadosProtocoloPreCadastro.") &&
            c.Campo != "configuracao.urlParaRetorno").ToList();
        void Row(string key, string value, string state) => campos.Add(new(key, value, state, origem));
        var marker = codigoTransportadoraPadrao?.Trim();
        var carrier = !string.IsNullOrWhiteSpace(codigoTransportadoraPadrao) && codigoTransportadoraPadrao.Length <= 20 &&
            !new[] { Pending, "NAO VALIDADO", "PENDENTE" }.Contains(marker, StringComparer.OrdinalIgnoreCase)
            ? codigoTransportadoraPadrao : Pending;
        var callback = Uri.TryCreate(urlParaRetorno, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            ? urlParaRetorno! : Pending;
        Row("dadosProtocoloPreCadastro.id", "0", "CRIAÇÃO; ID RETORNADO É A IDENTIDADE DO PROTOCOLO");
        Row("dadosProtocoloPreCadastro.codigoTransportadora", carrier, carrier == Pending ?
            "PADRÃO NÃO CONFIGURADO — BLOQUEADO" : "PADRÃO PROVISÓRIO HML; ALTERADO PELO OPERADOR NO GUARDIAN");
        Row("dadosProtocoloPreCadastro.codigoEmissor", Pending, "EMISSOR ACM NÃO COMPROVADO; NÃO COPIAR 10000 DO EXEMPLO");
        Row("dadosProtocoloPreCadastro.numeroReferencia", "OC1031", "REFERÊNCIA DOCUMENTAL — NÃO É IDEMPOTÊNCIA; NÃO COPIAR OC10");
        Row("configuracao.urlParaRetorno", callback, callback == Pending ? "CALLBACK PENDENTE" :
            "WEBHOOK DE TESTE INFORMADO; NÃO É CORRELAÇÃO DURÁVEL DO FUGATES");
        Row("dadosProtocoloPreCadastro.camposAdicionais[1]", "interno", "PADRÃO PARCIAL INFORMADO PELO USUÁRIO");
        foreach (var key in new[] { "codigoFluxo", "codigoItem", "codigoContrato", "idAgenda" })
            Row("dadosProtocoloPreCadastro." + key, "OMITIDO", "AUSENTE NO BODY ACEITO; NULL NO RETORNO INFORMADO");
        Row("placas/identificador", "OMITIDOS", "DADOS DO EXEMPLO NÃO SÃO DADOS SAP DA OPERAÇÃO");
        Row("vinculoContrato", Pending, "PRÉ-CADASTRO PARCIAL NÃO COMPROVA CADASTRO OU VÍNCULO DO CONTRATO");
        var dto = new GuardianProtocoloParcialRequestDto(new(callback),
            new(0, "OC1031", Pending, carrier, [new(1, "interno")]));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return new(campos, JsonSerializer.Serialize(dto, options), baseline.SapStatus);
    }

    public static AcmRestPreviewR53 Criar(ContratoSapToledoSnapshot? sap=null,int? versao=null,string? evidencia=null)
    {
        const string baseline="R52 + Descritivo REV1 p.11 + Swagger Guardian; documental, não consulta nesta sessão";
        var campos=new List<AcmPreviewFieldR53>();
        void Row(string key,string value,string state,string origin=baseline)=>campos.Add(new(key,value,state,origin));
        Row("sap.centro","3011","BASELINE DOCUMENTAL");
        Row("sap.contratoSolicitado","0000001031","ALVO INFORMADO — NÃO CONSULTADO");
        Row("sap.contratoRespostaLiteral","1031","BASELINE DOCUMENTAL — ENVIO NÃO APROVADO");
        Row("sap.versaoInterna","2","BASELINE DOCUMENTAL — NÃO INCLUIR NO JSON");
        Row("sap.codigoItem","000000000007000000","BASELINE DOCUMENTAL — IDENTIDADE GUARDIAN PENDENTE");
        Row("sap.codigoEmissor","0020000184","BASELINE DOCUMENTAL — IDENTIDADE GUARDIAN PENDENTE");
        Row("dadosProtocoloPreCadastro.id","0","CONFIRMADO NO BASELINE R52");
        Row("dadosProtocoloPreCadastro.idAgenda","1","BASELINE DOCUMENTAL R52");
        Row("dadosProtocoloPreCadastro.camposAdicionais[1000]","AC3P","CONFIRMADO NO BASELINE R52");
        Row("dadosProtocoloPreCadastro.camposAdicionais[1003]","10","CONFIRMADO NO BASELINE R52");
        Row("dadosProtocoloPreCadastro.codigoFluxo","FLX_RECEBIMENTO","REGRA DOCUMENTAL COMPROVADA");
        Row("dadosProtocoloPreCadastro.numeroReferencia","OC1031","REFERÊNCIA DOCUMENTAL — NÃO É IDEMPOTÊNCIA");
        Row("dadosProtocoloPreCadastro.codigoTransportadora",Pending,"PADRÃO OBRIGATÓRIO — CONFIGURAÇÃO PENDENTE","Decisão funcional corrigida: enviar padrão e substituir no Guardian");
        foreach(var key in new[]{"codigoItem","codigoEmissor","codigoContrato"})
            Row("dadosProtocoloPreCadastro."+key,Pending,"PENDENTE — NÃO OPERACIONAL");
        Row("configuracao.urlParaRetorno",Pending,"PENDENTE — CALLBACK NÃO APROVADO");
        Row("dadosProtocoloPreCadastro.camposAdicionais[1002]","3011","REGRA COMPROVADA: CENTRO SAP; VALOR DOCUMENTAL");
        Row("opcionais/documentos",Pending,"OPCIONAIS NO DESCRITIVO/SWAGGER — COMPLEMENTAÇÃO PELO AGENTE");
        Row("versaoContrato","NÃO INCLUÍDO","OMITIDO — PROPRIEDADE NÃO CONTRATADA");
        var valid=sap is not null&&(sap.NumeroContrato=="0000001031"||sap.NumeroContrato=="1031")&&sap.Centro=="3011"&&versao==2&&!string.IsNullOrWhiteSpace(evidencia);
        if(valid){Row("sessao.sap.contrato",sap!.NumeroContrato,"OBSERVADO NA FONTE",evidencia!);Row("sessao.sap.material",sap.Material??Pending,"OBSERVADO; MAPEAMENTO GUARDIAN PENDENTE",evidencia!);}
        var data=new PreCadastroRecebimentoGuardianSet26(0,"FLX_RECEBIMENTO",Pending,Pending,Pending,Pending,1,"OC1031",Pending,[new(1000,"AC3P"),new(1002,"3011"),new(1003,"10")]);
        var dto=new GuardianPreCadastroRequestDto(new(Pending),data);
        var resolver=new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type=>{if(type.Type==typeof(PreCadastroRecebimentoGuardianSet26))foreach(var property in type.Properties)
            if(property.Name.Equals("documentos",StringComparison.OrdinalIgnoreCase))property.ShouldSerialize=(_,_)=>false;});
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true,TypeInfoResolver=resolver,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
        return new(campos,JsonSerializer.Serialize(dto,options),valid?"DADOS SAP OBSERVADOS; MAPEAMENTO REST PENDENTE":sap is null?
            "NÃO CONSULTADO NESTA SESSÃO — cliente autorizado de 1031/V2 indisponível":"BLOQUEADO — identidade/versão/origem SAP não comprovada");
    }
}
