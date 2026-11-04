using System.Globalization;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;

using FugaTES.Aplicacao.Configuracao;
namespace FugaTES.Aplicacao.Servicos.Mvp;

public interface IAcmGuardianBusinessFailure { int? BusinessErrorCode { get; } }
public interface IAcmGuardianHttpFailure
{
    int HttpStatusCode { get; }
    string SafeCode { get; }
    string? SafeBusinessMessage { get; }
    bool ResultadoIndeterminado { get; }
}
public interface IAcmR57RuntimeGate
{
    string? Bloqueio(VersaoContratoSapToledo versao);
    bool ExigeContratoNovo => false;
    bool CorrelationPermitida(string correlationId) => true;
}
// A functional approval is not a runtime authorization. No permissive runtime implementation is registered.
public sealed class AcmR57RuntimeDisabled : IAcmR57RuntimeGate
{
    public string? Bloqueio(VersaoContratoSapToledo versao) => "R57_RUNTIME_WRITE_DISABLED";
}
public interface IAcmR57Preparacao { AcmR57Vinculos Obter(VersaoContratoSapToledo versao); }
public sealed class AcmR57PreparacaoPendente : IAcmR57Preparacao
{
    public AcmR57Vinculos Obter(VersaoContratoSapToledo versao) => throw new AcmR57BlockedException("R57_FUNCTIONAL_SOURCES_REQUIRED");
}
public sealed class AcmR57BlockedException(string code) : Exception(code) { public string Code { get; }=code; }

// Every value is scoped to the immutable inbound fingerprint, SAP item and centre.
// Sources are references, never credentials. No BP/material identity fallback.
public sealed record AcmR57Vinculos(string NumeroContratoSap,string? ItemSap,string CentroSap,string FingerprintSap,
    string CodigoContratoGuardian,string EstadoGuardian,string TipoLancamentoGuardian,
    string SapBusinessPartner,string SapMaterial,string CodigoEmissorGuardian,string CodigoItemGuardian,
    string DocumentoOperacao,decimal PesoOrigem,string NumeroReferencia,string ValorCampoAdicional,
    string CallbackOperacional,string FonteMapeamentos,string FonteDocumento,string FontePeso,
    string FonteCampoAdicional,bool CallbackHomologado)
{
    public AcmR104RestBinding? RestBinding { get; init; }
    public AcmR104CallbackBinding? CallbackBinding { get; init; }
    public AcmR104CorrelationBinding? CorrelationBinding { get; init; }
}

public sealed record DocumentoAcmR57(string Descricao,string PesoOrigem);
public sealed record DadosPreCadastroAcmR57(int Id,string CodigoItem,string CodigoEmissor,string CodigoContrato,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DocumentoAcmR57>? Documentos,string NumeroReferencia,IReadOnlyList<CampoAdicionalGuardianSet26> CamposAdicionais)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? CodigoTransportadora { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.Never)] public string? CodigoFluxo { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? IdAgenda { get; init; }
}
public sealed record PreCadastroAcmR57(string Callback,DadosPreCadastroAcmR57 Dados)
{
    // Evidence from the authorized HML callback configuration; default is not approved.
    // Never transmitted as an API property or used to relax the runtime write gate.
    [JsonIgnore] public bool CallbackHomologado { get; init; }
    [JsonIgnore] public AcmR104CallbackBinding? CallbackBinding { get; init; }
    [JsonIgnore] public bool CriacaoInicialSemDadosPesagem { get; init; }
}
public sealed record ProtocoloAcmR57(long Id,string CodigoContrato,string CodigoItem,string CodigoEmissor,
    string NumeroReferencia,string? NumeroTicket=null);
public interface IAcmPreCadastroMinimoGatewayR57
{
    Task<ProtocoloAcmR57> EnviarMinimoAcmAsync(PreCadastroAcmR57 comando,CancellationToken ct);
}
public sealed record AcmR57Plano(ContratoGuardianSet26 Contrato,PreCadastroAcmR57 Protocolo);

public static class AcmR57Mapping
{
    public static AcmR57Plano Criar(VersaoContratoSapToledo version,AcmR57Vinculos links)
    {
        var s=version.Snapshot;
        if(version.IntegracaoId==Guid.Empty || version.Versao<=0 || version.NumeroContrato!=s.NumeroContrato ||
            links.NumeroContratoSap!=s.NumeroContrato || links.ItemSap!=s.Item || links.CentroSap!=s.Centro ||
            links.FingerprintSap!=version.FingerprintSha256 || links.SapBusinessPartner!=s.IdParceiro || links.SapMaterial!=s.Material)
            throw new AcmR57BlockedException("R57_SOURCE_IDENTITY_MISMATCH");
        if(new FugaTES.Aplicacao.Servicos.ContratosSapToledo.ContratoSapToledoCanonicalizer().Canonicalizar(s).FingerprintSha256!=version.FingerprintSha256)
            throw new AcmR57BlockedException("R57_SOURCE_FINGERPRINT_MISMATCH");
        // R89: Item/UnidCont are optional in historical H4. When Item is absent,
        // ItemSap must also be absent: the identity comparison above remains strict.
        // Neither optional field is serialized into the approved SOAP/REST payloads.
        foreach(var v in new[]{s.NumeroContrato,s.Centro,s.TipoContrato,s.DescricaoTipoContrato,s.IdParceiro,s.Material,s.OperacaoContrato,s.StatusContrato})
            Require(v,160,"R57_INBOUND_REQUIRED_FIELDS");
        if(s.Item is not null) Require(s.Item,160,"R57_INBOUND_REQUIRED_FIELDS");
        if(s.UnidCont is not null) Require(s.UnidCont,160,"R57_INBOUND_REQUIRED_FIELDS");
        if(!CentrosAcm.EhPermitido(s.Centro) || s.TipoContrato!="AC3P" || s.QuantidadeContrato is null or <0)
            throw new AcmR57BlockedException("R57_INBOUND_SCENARIO_INVALID");
        Require(links.CodigoContratoGuardian,20,"R57_CONTRACT_CODE_UNAPPROVED");
        if(s.NumeroContrato.Any(c=>!char.IsAsciiDigit(c)) || links.CodigoContratoGuardian.Any(c=>!char.IsAsciiDigit(c)) ||
            s.NumeroContrato.TrimStart('0')!=links.CodigoContratoGuardian.TrimStart('0') || links.CodigoContratoGuardian.All(c=>c=='0'))
            throw new AcmR57BlockedException("R57_CONTRACT_REPRESENTATION_UNAPPROVED");
        // R69 homologation belongs to this contract/scenario only. Future contracts retain their scoped mappings.
        if(s.NumeroContrato.TrimStart('0')=="1106")
        {
            if(s.OperacaoContrato!="10" || s.StatusContrato!="T")
                throw new AcmR57BlockedException("R69_BUSINESS_SCENARIO_UNAPPROVED");
            if(links.CodigoEmissorGuardian!="20000184")
                throw new AcmR57BlockedException("R69_HOMOLOGATED_EMITTER_REQUIRED");
            if(links.CodigoContratoGuardian!="1106" || links.CodigoItemGuardian!="7000000" ||
                links.EstadoGuardian!="308102" || links.TipoLancamentoGuardian!="309837" || links.NumeroReferencia!="OC1106")
                throw new AcmR57BlockedException("R69_BUSINESS_APPROVAL_MISMATCH");
        }
        Require(links.CodigoEmissorGuardian,20,"R57_EMITTER_MAPPING_REQUIRED");
        Require(links.CodigoItemGuardian,30,"R57_ITEM_MAPPING_REQUIRED");
        foreach(var v in new[]{links.EstadoGuardian,links.TipoLancamentoGuardian,links.FonteMapeamentos})
            Require(v,160,"R57_PROVEN_FUNCTIONAL_SOURCE_REQUIRED");
        Require(links.NumeroReferencia,10,"R57_REFERENCE_INVALID");
        IReadOnlyList<DocumentoAcmR57>? documents=null;
        if(!string.IsNullOrEmpty(links.DocumentoOperacao))
        {
            Require(links.DocumentoOperacao,40,"R57_OPERATION_DOCUMENT_REQUIRED");
            Require(links.FonteDocumento,160,"R57_PROVEN_FUNCTIONAL_SOURCE_REQUIRED");
            Require(links.FontePeso,160,"R57_PROVEN_FUNCTIONAL_SOURCE_REQUIRED");
            var weight=links.PesoOrigem.ToString("G29",CultureInfo.InvariantCulture);
            if(links.PesoOrigem<0 || weight.Length>13)throw new AcmR57BlockedException("R57_ORIGIN_WEIGHT_INVALID");
            documents=[new(links.DocumentoOperacao,weight)];
        }
        else if(!string.IsNullOrEmpty(links.FonteDocumento) || !string.IsNullOrEmpty(links.FontePeso) || links.PesoOrigem!=0m)
            throw new AcmR57BlockedException("R69_DOCUMENT_SOURCE_PARTIAL");
        if(!AcmR104CallbackPolicy.Validar(links.CallbackOperacional,links.CallbackHomologado,links.CallbackBinding))
            throw new AcmR57BlockedException("R57_CALLBACK_NOT_APPROVED");
        var rest=links.RestBinding;
        if(rest is null || rest.IntegracaoId!=version.IntegracaoId || rest.VersaoH4!=version.Versao ||
            rest.Fingerprint!=version.FingerprintSha256 || rest.NumeroContratoSap!=version.NumeroContrato)
            throw new AcmR57BlockedException("R104_REST_BINDING_REQUIRED");
        Require(rest.CodigoTransportadora,20,"R104_CARRIER_REQUIRED");
        Require(rest.CodigoFluxo,50,"R104_FLOW_REQUIRED");
        Require(rest.FonteTransportadora,160,"R104_CARRIER_EVIDENCE_REQUIRED");
        Require(rest.FonteFluxo,160,"R104_FLOW_EVIDENCE_REQUIRED");
        var contract=new ContratoGuardianSet26(links.CodigoContratoGuardian,s.DescricaoTipoContrato!,s.QuantidadeContrato.Value,
            links.EstadoGuardian,0m,links.TipoLancamentoGuardian){CodigoEmissor=links.CodigoEmissorGuardian,CodigoItem=links.CodigoItemGuardian};
        var request=new PreCadastroAcmR57(links.CallbackOperacional,new(0,links.CodigoItemGuardian,links.CodigoEmissorGuardian,
            links.CodigoContratoGuardian,documents,links.NumeroReferencia,
            [new(1000,s.TipoContrato!),new(1002,s.Centro!),new(1003,s.OperacaoContrato!)])
            {CodigoTransportadora=rest.CodigoTransportadora,CodigoFluxo=rest.CodigoFluxo})
            {CallbackHomologado=links.CallbackHomologado,CallbackBinding=links.CallbackBinding};
        Validar(request);
        return new(contract,request);
    }

    public static void Validar(PreCadastroAcmR57 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(!AcmR104CallbackPolicy.Validar(request.Callback,request.CallbackHomologado,request.CallbackBinding))
            throw new AcmR57BlockedException("R57_CALLBACK_NOT_APPROVED");
        var d=request.Dados;
        if(d is null || d.Id!=0)throw new AcmR57BlockedException("R57_REST_ID_INVALID");
        Require(d.CodigoContrato,20,"R57_REST_CONTRACT_REQUIRED");Require(d.CodigoItem,30,"R57_ITEM_MAPPING_REQUIRED");
        Require(d.CodigoEmissor,20,"R57_EMITTER_MAPPING_REQUIRED");Require(d.NumeroReferencia,10,"R57_REFERENCE_INVALID");
        if (request.CriacaoInicialSemDadosPesagem)
        {
            if (d.CodigoTransportadora is not null || d.CodigoFluxo is not null || d.Documentos is not null || d.IdAgenda is not (0 or 1))
                throw new AcmR57BlockedException("R165U_INITIAL_PROTOCOL_FIELDS_INVALID");
        }
        else
        {
            Require(d.CodigoTransportadora,20,"R104_CARRIER_REQUIRED");
            Require(d.CodigoFluxo,50,"R104_FLOW_REQUIRED");
        }
        if(d.Documentos is not null)
        {
            if(d.Documentos.Count!=1 || d.Documentos[0] is null)throw new AcmR57BlockedException("R57_OPERATION_DOCUMENT_REQUIRED");
            Require(d.Documentos[0].Descricao,40,"R57_OPERATION_DOCUMENT_REQUIRED");Require(d.Documentos[0].PesoOrigem,13,"R57_ORIGIN_WEIGHT_INVALID");
            if(!decimal.TryParse(d.Documentos[0].PesoOrigem,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var weight) || weight<0)
                throw new AcmR57BlockedException("R57_ORIGIN_WEIGHT_INVALID");
        }
        if(d.CamposAdicionais is null || d.CamposAdicionais.Count!=3 || d.CamposAdicionais.Any(c=>c is null) ||
            !d.CamposAdicionais.Select(c=>c.Numero).Order().SequenceEqual(new[]{1000,1002,1003}))
            throw new AcmR57BlockedException("R57_ADDITIONAL_FIELD_INVALID");
        foreach(var field in d.CamposAdicionais)Require(field.Valor,50,"R57_ADDITIONAL_VALUE_REQUIRED");
        if(!request.CriacaoInicialSemDadosPesagem && d.CodigoContrato.TrimStart('0')=="1106" && (d.CodigoContrato!="1106" || d.CodigoEmissor!="20000184" ||
            d.CodigoItem!="7000000" || d.NumeroReferencia!="OC1106" ||
            !d.CamposAdicionais.Any(c=>c.Numero==1000&&c.Valor=="AC3P") ||
            !d.CamposAdicionais.Any(c=>c.Numero==1002&&c.Valor=="3011") ||
            !d.CamposAdicionais.Any(c=>c.Numero==1003&&c.Valor=="10")))
            throw new AcmR57BlockedException("R69_BUSINESS_APPROVAL_MISMATCH");
    }
    public static bool RespostaConfere(ProtocoloAcmR57 response,PreCadastroAcmR57 request)
        => response.Id>0 && response.CodigoContrato==request.Dados.CodigoContrato && response.CodigoItem==request.Dados.CodigoItem &&
           response.CodigoEmissor==request.Dados.CodigoEmissor && response.NumeroReferencia==request.Dados.NumeroReferencia &&
           (response.NumeroTicket is null || !string.IsNullOrWhiteSpace(response.NumeroTicket));
    private static void Require(string? value,int max,string code)
    {
        if(string.IsNullOrWhiteSpace(value) || value!=value.Trim() || value.Length>max || value.Any(char.IsControl) ||
           new[]{"NÃO VALIDADO","NAO VALIDADO","PENDENTE"}.Contains(value,StringComparer.OrdinalIgnoreCase))
            throw new AcmR57BlockedException(code);
    }
}
