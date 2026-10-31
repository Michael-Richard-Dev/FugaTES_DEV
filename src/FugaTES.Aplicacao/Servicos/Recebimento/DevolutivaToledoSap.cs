using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Servicos.Recebimento;

// Full Toledo document return; does not reinterpret a receipt ID as a ticket/protocol/H4 ID.
public static class ToledoSapWork
{
    public const string Operacao = "SAP_DEVOLUTIVA_TOLEDO_DOCUMENTO";
    public static string Fingerprint(Guid receipt, string originalHash) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Operacao}:v1:{receipt:D}:{originalHash}")));
    public static CandidatoIntegracao Candidato(Guid receipt, string originalHash) =>
        new("GUARDIAN", "SAP", Operacao, receipt.ToString("D"), Fingerprint(receipt, originalHash), $"toledo-sap:{receipt:D}");
}

public sealed record DocumentoToledoSap(Guid ReciboId, string Centro, string IdPesagem,
    string DocumentoJson, string FingerprintOriginal, bool OrigemComprovada, bool Sintetico)
{
    public ToledoDeliveryPartnerProof? DeliveryPartners {get;init;}
    public DateTimeOffset RecebidoEm {get;init;}
    public IReadOnlyList<ToledoSapContratoFonte> ContratosSap {get;init;}=[];
    public bool Valido()
    {
        if (ReciboId == Guid.Empty || Sintetico || !OrigemComprovada || string.IsNullOrWhiteSpace(Centro) ||
            string.IsNullOrWhiteSpace(IdPesagem) || string.IsNullOrWhiteSpace(DocumentoJson)) return false;
        try
        {
            using var json = JsonDocument.Parse(DocumentoJson, new() { MaxDepth = 32 });
            var root = json.RootElement.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value,StringComparer.OrdinalIgnoreCase);
            static string? Identifier(JsonElement x)=>x.ValueKind==JsonValueKind.String?x.GetString():x.ValueKind==JsonValueKind.Number?x.GetRawText():null;
            return Encoding.UTF8.GetByteCount(DocumentoJson)<=1048576 && Centro.All(char.IsAsciiDigit) && IdPesagem.All(char.IsAsciiDigit) &&
                string.Equals(root["SistemaBalanca"].GetString(),"Toledo",StringComparison.OrdinalIgnoreCase) &&
                Identifier(root["Centro"]) == Centro && Identifier(root["IdPesagem"]) == IdPesagem &&
                root["to_Pesagem"].GetArrayLength() > 0 &&
                RegistrarPesagemToledoCasoDeUso.Fingerprint(DocumentoJson) == FingerprintOriginal;
        }
        catch { return false; }
    }
}

public enum ResultadoToledoSapCodigo
{
    PreEnvioBloqueado, FalhaAntesDoPost, RejeicaoHttp, ResultadoIndeterminado, AceiteHttpSemConfirmacaoEmpresarial, RecebimentoConfirmadoSap, RecebimentoJaConfirmadoSap, RegistroSapExistenteDivergente
}
public sealed record RetornoToledoSap(ResultadoToledoSapCodigo Codigo, int? HttpStatus, bool PostIniciado)
{
    public bool Indeterminado => Codigo is ResultadoToledoSapCodigo.ResultadoIndeterminado or ResultadoToledoSapCodigo.AceiteHttpSemConfirmacaoEmpresarial;
    public string? EntityId {get;init;}
    public string? RequestSha256 {get;init;}
    public string? ResponseSha256 {get;init;}
    public string? PayloadVersion {get;init;}
    public string? DetailCode {get;init;}
    public bool Confirmado=>((Codigo==ResultadoToledoSapCodigo.RecebimentoConfirmadoSap&&HttpStatus==201&&PostIniciado)||
        (Codigo==ResultadoToledoSapCodigo.RecebimentoJaConfirmadoSap&&HttpStatus==200&&!PostIniciado))&&
        Uri.TryCreate(EntityId,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.UserInfo.Length==0&&
        RequestSha256 is{Length:64}&&RequestSha256.All(char.IsAsciiHexDigit)&&ResponseSha256 is{Length:64}&&ResponseSha256.All(char.IsAsciiHexDigit)&&
        PayloadVersion=="TOLEDO_SAP_ODATA_V1";
}
public sealed record PlanoEnvioToledoSap(string RequestSha256,string PayloadVersion,long EpochMilliseconds)
{
    public ToledoDeliveryPartnerProof? DeliveryPartners {get;init;}
    public string? ParceirosVersao {get;init;}
    public IReadOnlyList<ToledoSapParceiroAuditado> ParceirosContrato {get;init;}=[];
    public bool ParceirosComprovados=>ParceirosVersao==ToledoSapContractPartners.Version&&ParceirosContrato.Count is >0 and <=1000&&
      ParceirosContrato.All(x=>x is not null&&x.Valido)&&ParceirosContrato.Select(x=>x.Indice).Distinct().Count()==ParceirosContrato.Count;
    public bool Valido=>RequestSha256 is{Length:64}&&RequestSha256.All(char.IsAsciiHexDigit)&&PayloadVersion=="TOLEDO_SAP_ODATA_V1";
}
public interface IDocumentoToledoSapGateway
{
    Task<RetornoToledoSap> EnviarOriginalAsync(DocumentoToledoSap documento, Func<PlanoEnvioToledoSap,CancellationToken,Task<bool>> autorizarPost, CancellationToken ct);
}
public interface IDevolutivaToledoSapStore
{
    Task<DocumentoToledoSap?> ObterAsync(ClaimIntegracaoMotor claim, CancellationToken ct);
    // Must persist a fenced send intent before any SAP request. False means NO dispatch.
    Task<bool> MarcarDespachoAsync(ClaimIntegracaoMotor claim, CancellationToken ct);
    Task RegistrarRetornoAsync(ClaimIntegracaoMotor claim, RetornoToledoSap retorno, CancellationToken ct);
    Task<bool> ValidarDespachoAsync(ClaimIntegracaoMotor claim,PlanoEnvioToledoSap plano,CancellationToken ct);
}

// Invoked by the existing motor only; no untracked Task.Run, HTTP in the UI, or new motor.
public sealed class ProcessadorDocumentoToledoSap(IDevolutivaToledoSapStore store, IDocumentoToledoSapGateway sap)
    : IProcessadorExternoIntegracao
{
    public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
    {
        if (claim.Origem != "GUARDIAN" || claim.Destino != "SAP" || claim.Operacao != ToledoSapWork.Operacao ||
            !Guid.TryParseExact(claim.ChaveNegocio, "D", out var receipt) || receipt == Guid.Empty)
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
        var document = await store.ObterAsync(claim, ct);
        if (document is null || document.ReciboId != receipt || !document.Valido() ||
            claim.CorrelationId != $"toledo-sap:{receipt:D}" ||
            claim.FingerprintSha256 != ToledoSapWork.Fingerprint(receipt, document.FingerprintOriginal) ||
            !await store.MarcarDespachoAsync(claim, ct))
        {
            await store.RegistrarRetornoAsync(claim, new(ResultadoToledoSapCodigo.PreEnvioBloqueado, null, false), ct);
            return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
        }
        RetornoToledoSap result;
        try { result = await sap.EnviarOriginalAsync(document,(plan,token)=>store.ValidarDespachoAsync(claim,plan,token),ct); }
        catch { result = new(ResultadoToledoSapCodigo.ResultadoIndeterminado, null, true); }
        // An audit/storage exception is allowed to reach the motor's fenced unknown-outcome path.
        await store.RegistrarRetornoAsync(claim, result, ct);
        if(result.Confirmado)return ResultadoProcessamentoExterno.Sucesso;
        if(result.Codigo is ResultadoToledoSapCodigo.RecebimentoConfirmadoSap or ResultadoToledoSapCodigo.RecebimentoJaConfirmadoSap)
            return new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado);
        return new(result.Indeterminado ? ClassificacaoFalhaIntegracao.ResultadoIndeterminado : ClassificacaoFalhaIntegracao.FalhaDefinitiva);
    }
}

public sealed record DevolutivaToledoSapSnapshot(Guid ReciboId, Guid IntegracaoId, string Estado,
    string Codigo, int? HttpStatus, int Tentativas, bool EmExecucao, bool ResultadoIndeterminado,
    bool OrigemComprovada, bool Sintetico, bool FalhaConhecidaSemEfeito, string Fingerprint,
    string Revisao, DateTimeOffset AtualizadoEm);
public sealed record DecisaoReenvioToledoSap(bool Permitido, string Motivo);
public static class DevolutivaToledoSapPolicy
{
    public static DecisaoReenvioToledoSap Avaliar(DevolutivaToledoSapSnapshot? s, bool comandoAutorizado)
    {
        DecisaoReenvioToledoSap Block(string message) => new(false, message);
        if (s is null) return Block("Devolutiva automática SAP ainda não vinculada a este recibo; não há tentativa para reenviar.");
        if (s.ResultadoIndeterminado || s.Estado == "RESULTADO_INDETERMINADO" || s.Codigo is "ResultadoIndeterminado" or "AceiteHttpSemConfirmacaoEmpresarial")
            return Block("SAP pode já ter recebido a pesagem. Reconciliar antes de qualquer reenvio.");
        if (s.Estado == "CONCLUIDA" || s.Codigo is "RecebimentoConfirmadoSap" or "RecebimentoJaConfirmadoSap" or "RegistroSapExistenteDivergente") return Block("Devolutiva concluída; não reenviar.");
        if (s.EmExecucao || s.Estado is "PENDENTE" or "EM_PROCESSAMENTO" or "AGUARDANDO_REPROCESSAMENTO")
            return Block("Envio já ativo ou na fila; aguarde o resultado.");
        if (s.Sintetico || !s.OrigemComprovada || s.ReciboId == Guid.Empty || s.IntegracaoId == Guid.Empty || s.Tentativas < 1 ||
            !Hash(s.Fingerprint) || !Hash(s.Revisao)) return Block("Identidade, origem ou histórico persistido insuficientes.");
        if (s.Estado is not ("FALHA_DEFINITIVA" or "AGUARDANDO_ACAO_MANUAL") || !s.FalhaConhecidaSemEfeito ||
            s.Codigo != nameof(ResultadoToledoSapCodigo.FalhaAntesDoPost))
            return Block("Falha exige correção/reconciliação comprovada; este botão não altera o JSON original.");
        if (!comandoAutorizado) return Block("Serviço de comando SAP e autorização do operador ainda não estão disponíveis.");
        return new(true, "Falha comprovada antes do POST. Reenviar o mesmo documento persistido, com justificativa e auditoria.");
    }
    private static bool Hash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed record PreviaReenvioToledoSap(DevolutivaToledoSapSnapshot Snapshot, DecisaoReenvioToledoSap Decisao);
public sealed record ComandoReenvioToledoSap(Guid ComandoId, Guid ReciboId, Guid IntegracaoId, string Revisao,
    string Justificativa, bool Confirmado);
public sealed record ReciboComandoToledoSap(Guid ComandoId, Guid ReciboId, string Estado);
// The command server must repeat eligibility under lock, authenticate the operator and audit it atomically.
// A read-side decision or a boolean from the client is NEVER a server-side approval.
public interface IComandoReenvioToledoSapClient
{
    bool Configurado { get; }
    Task<PreviaReenvioToledoSap> PrepararAsync(Guid reciboId, CancellationToken ct);
    Task<ReciboComandoToledoSap> SolicitarAsync(ComandoReenvioToledoSap comando, CancellationToken ct);
}
