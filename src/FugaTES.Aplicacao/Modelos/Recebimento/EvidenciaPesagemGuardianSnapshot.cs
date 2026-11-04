using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Servicos.Mvp;

using FugaTES.Aplicacao.Configuracao;
namespace FugaTES.Aplicacao.Modelos.Recebimento;

// This is an internal audit snapshot of the existing SAP input, NOT a second wire mapper.
public sealed record EvidenciaPesagemGuardianSnapshot(string Json, string Sha256)
{
    public static EvidenciaPesagemGuardianSnapshot Criar(CorrelacaoRecebimentoMvp c, PesagemSapMvp p)
    {
        ArgumentNullException.ThrowIfNull(c);ArgumentNullException.ThrowIfNull(p);
        var e=p.EvidenciaTicket;
        if(!CentrosAcm.EhPermitido(c.Entrada.Centro) || c.Entrada.TipoContrato!="AC3P" ||
           !AcmR106SapReturnPreflight.EventoFinalComprovado(c) || e is null ||
           p.SistemaBalanca!="Toledo" || p.TipoDocumento!=c.Entrada.TipoContrato || p.DocumentoSap!=c.DocumentoSap ||
           e.CallbackFingerprint!=c.CallbackFingerprint || e.ProtocoloGuardian!=c.ProtocoloGuardian ||
           e.TicketGuardian!=c.TicketGuardian || p.IdPesagem!=c.TicketGuardian || p.NumeroProtocolo!=c.ProtocoloGuardian ||
           e.ContratoACM!=c.Entrada.ContratoAcmSap || p.ContratoACM!=c.Entrada.ContratoAcmSap ||
           e.Centro!=c.Entrada.Centro || p.Centro!=c.Entrada.Centro ||
           e.Material!=c.Entrada.Material || p.Material!=c.Entrada.Material ||
           e.BusinessPartnerContrato!=c.Entrada.BusinessPartnerEmissor || p.IdParceiro!=c.Entrada.BusinessPartnerEmissor ||
           c.Pesos is not {} weights || p.PesoBruto!=weights.PesoBruto || p.PesoTara!=weights.Tara || p.PesoAntesDesconto!=weights.PesoLiquido ||
           e.TipoOperacaoSap is not >0 || p.TipoOperacao!=e.TipoOperacaoSap || string.IsNullOrWhiteSpace(e.EvidenceReference) ||
           e.TipoOperacaoSapProveniencia is not ("TICKET_BUSINESS_FIELD_ATTESTED" or "SAP_BUSINESS_RULE_ATTESTED") ||
           string.IsNullOrWhiteSpace(e.TipoOperacaoSapEvidenceReference) ||
           AcmSapWeighingDirection.ToSap(p.TipoPesagem)!=AcmSapWeighingDirection.ToSap(e.TipoPesagem) ||
           AcmSapWeighingDirection.ToSap(e.TipoPesagem) is null)
            throw new InvalidOperationException("R165A_EVIDENCE_CORRELATION_INVALID");
        var json=JsonSerializer.Serialize(new{Versao=1,Pesagem=p});
        var bytes=Encoding.UTF8.GetBytes(json);
        if(bytes.Length>2_000_000)throw new InvalidOperationException("R165A_EVIDENCE_TOO_LARGE");
        return new(json,Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public bool ConfirmarExistente(string? storedJson,string? storedHash,bool hasReceivedTime)
    {
        if(storedJson is null && storedHash is null && !hasReceivedTime)return false;
        if(storedJson!=Json || storedHash!=Sha256 || !hasReceivedTime)
            throw new InvalidOperationException("R165A_EVIDENCE_CONFLICT_OR_INCOMPLETE");
        return true;
    }
}
