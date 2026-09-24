using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.CasosDeUso.Recebimento;

public static class GuardianPreCadastroMotorWork
{
    public const string Operacao = "SAP_RECEBIMENTO_GUARDIAN_PRECADASTRO";

    public static string Fingerprint(string documento) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Operacao}:v1:{documento}")));
}

public sealed class RegistrarPreCadastroGuardianMotorCasoDeUso(
    IRepositorioCorrelacaoRecebimento correlacoes,
    IMotorIntegracaoRepository motor,
    GuardianSet26Options opcoes)
{
    public async Task<ResultadoRegistroSnapshotMvp> RegistrarAsync(RecebimentoSapCpiMvp entrada, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (new[] { entrada.Material, entrada.BusinessPartnerEmissor, entrada.BusinessPartnerTransportadora,
                entrada.Contrato, entrada.DocumentoLogisticoReferencia, entrada.TipoContrato, entrada.Centro,
                entrada.OperacaoContrato, opcoes.UrlParaRetorno, opcoes.EndpointEspecifico }.Any(string.IsNullOrWhiteSpace) ||
            entrada.Processo != "RECEBIMENTO" || entrada.CodigoFluxo != "FLX_RECEBIMENTO")
            throw new ArgumentException("Guardian pre-registration context is incomplete.", nameof(entrada));

        var snapshot = await correlacoes.RegistrarSnapshotAsync(
            entrada with { UrlParaRetorno = opcoes.UrlParaRetorno }, cancellationToken);
        if (snapshot.Status == StatusRegistroSnapshotMvp.Conflito)
            throw new InvalidOperationException("Conflicting receipt snapshot was rejected.");

        var documento = entrada.DocumentoLogisticoReferencia;
        if (!string.IsNullOrWhiteSpace(entrada.ContratoAcmSap))
        {
            var contrato = await correlacoes.AssociarContratoAcmAsync(
                documento, entrada.ContratoAcmSap, cancellationToken);
            if (contrato.Status == StatusAssociacaoContratoAcmMvp.Conflito)
                throw new InvalidOperationException("Conflicting ACM contract was rejected.");
        }
        await motor.RegistrarCandidatoAsync(
            new CandidatoIntegracao("SAP", "GUARDIAN", GuardianPreCadastroMotorWork.Operacao,
                documento, GuardianPreCadastroMotorWork.Fingerprint(documento), documento),
            DateTimeOffset.UtcNow, cancellationToken);
        return snapshot;
    }
}
