using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Integracao.Toledo.Set26;

public static class MapeadorGuardianSet26
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static string SerializarEmissor(EmissorGuardianSet26 emissor) { ArgumentNullException.ThrowIfNull(emissor); ValidarTexto(emissor.Codigo,20,false,"codigo"); ValidarTexto(emissor.Descricao,50,true,"descricao"); ValidarTexto(emissor.RazaoSocial,50,true,"razaoSocial"); if(emissor.Estado is not ("0" or "1")||emissor.TipoDocumento is not ("0" or "1"))throw new ArgumentException("Guardian emissor state or document type is invalid."); return JsonSerializer.Serialize(emissor,JsonOptions); }
    public static string SerializarTransportadora(TransportadoraGuardianSet26 transportadora) { ArgumentNullException.ThrowIfNull(transportadora); ValidarTexto(transportadora.Codigo,20,false,"codigo"); ValidarTexto(transportadora.Descricao,50,true,"descricao"); ValidarTexto(transportadora.RazaoSocial,50,true,"razaoSocial"); return JsonSerializer.Serialize(transportadora,JsonOptions); }
    public static PreCadastroRecebimentoGuardianSet26 MapearPreCadastro(RecebimentoSapCpiMvp entrada, GuardianSet26Options opcoes) { ArgumentNullException.ThrowIfNull(entrada); ArgumentNullException.ThrowIfNull(opcoes); if(string.IsNullOrWhiteSpace(opcoes.EndpointEspecifico)||string.IsNullOrWhiteSpace(opcoes.UrlParaRetorno))throw new ArgumentException("Guardian endpoint or return URL is absent."); return new(0,"FLX_RECEBIMENTO",entrada.Material,entrada.BusinessPartnerEmissor,entrada.BusinessPartnerTransportadora,entrada.Contrato,1,entrada.DocumentoLogisticoReferencia,opcoes.UrlParaRetorno,[new(1000,entrada.TipoContrato),new(1002,entrada.Centro),new(1003,entrada.OperacaoContrato)]); }
    public static string SerializarPreCadastro(PreCadastroRecebimentoGuardianSet26 preCadastro) { ArgumentNullException.ThrowIfNull(preCadastro); return JsonSerializer.Serialize(new { configuracao=new { urlParaRetorno=preCadastro.UrlParaRetorno }, dadosProtocoloPreCadastro=preCadastro },JsonOptions); }
    private static void ValidarTexto(string? value,int maximo,bool obrigatorio,string campo) { if((obrigatorio&&string.IsNullOrWhiteSpace(value))||value?.Length>maximo)throw new ArgumentException($"Guardian field is invalid: {campo}"); }
}
