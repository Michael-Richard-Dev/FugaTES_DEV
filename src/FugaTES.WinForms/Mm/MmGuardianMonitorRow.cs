using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
namespace FugaTES.WinForms.Mm;
/// <summary>Projeção de observação. JSON SAP permanece intacto; JSON Guardian distingue prévia de envio reservado.</summary>
public sealed record MmGuardianMonitorRow(PedidoCompraMmLeitura Source,RegistroProtocoloMm? Registro,string? Callback,string Automacao)
{
    public string Empresa=>Source.Empresa;public string Pedido=>Source.Pedido;public string Item=>Source.Item;
    public string TipoPedido=>Source.TipoPedido;public string Centro=>Source.Centro;public string Material=>Source.Material;
    public string GrupoMaterial=>Source.GrupoMaterial;public decimal Quantidade=>Source.Quantidade;public string Unidade=>Source.Unidade;
    public string Fornecedor=>Source.Fornecedor;public string EstadoSap=>Source.EstadoSap;
    public string ProtocoloGuardian=>Registro?.ProtocoloId?.ToString()??"—";
    public string HttpGuardian=>Registro?.Http?.ToString()??"—";
    public DateTimeOffset? UltimaTentativaGuardian=>Registro?.AtualizadoUtc;
    public string? JsonGuardian
    {
        get
        {
            if(Registro is not null)return Registro.JsonEnviado;
            try{return MapeadorProtocoloMm.Serializar(MapeadorProtocoloMm.Montar(Source,Callback??""));}
            catch(InvalidOperationException){return null;}
        }
    }
    public string EstadoGuardian
    {
        get
        {
            if(Registro is null)
            {
                if(DataError is not null)return "BLOQUEADO_DADOS — "+DataError;
                return Automacao is "MM_GUARDIAN_VERIFICADO" or "MM_GUARDIAN_COM_BLOQUEIOS"?"AGUARDANDO":Automacao;
            }
            if(Registro.SourceSha256 is {} sourceHash&&MapeadorProtocoloMm.SourceHash(Source)!=sourceHash)return "ALTERADO_APOS_ENVIO — RECONCILIAR";
            try
            {
                using var original=System.Text.Json.JsonDocument.Parse(Registro.JsonEnviado);
                var url=original.RootElement.GetProperty("configuracao").GetProperty("urlParaRetorno").GetString()!;
                var atual=MapeadorProtocoloMm.Serializar(MapeadorProtocoloMm.Montar(Source,url));
                if(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(atual)))!=Registro.Sha256)return "ALTERADO_APOS_ENVIO — RECONCILIAR";
            }
            catch{return "REGISTRO_REQUER_CONFERENCIA";}
            return Registro.Estado;
        }
    }
    private string? DataError
    {
        get
        {
            if(string.IsNullOrWhiteSpace(Callback))return null;
            try{MapeadorProtocoloMm.Montar(Source,Callback);return null;}
            catch(InvalidOperationException ex){return ex.Message.StartsWith("MM_",StringComparison.Ordinal)?ex.Message:"MM_DADOS_INVALIDOS";}
        }
    }
    public bool RetryCandidate=>Registro is{Estado:EstadoProtocoloMm.Recusado,ProtocoloId:null}&&EstadoGuardian==EstadoProtocoloMm.Recusado;
    public string? MensagemGuardian=>Registro is{} r&&MmRetryPolicy.IsPrecheck(r.Mensagem)?MmRetryPolicy.Describe(r.Mensagem!):Registro?.Mensagem??(DataError is {} error?"Item bloqueado antes do POST: "+error:null);
}
