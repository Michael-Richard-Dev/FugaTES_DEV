using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.CasosDeUso.Recebimento;

public sealed class ProcessarRecebimentoMvpCasoDeUso(RegistrarPreCadastroGuardianMotorCasoDeUso registro)
{
    public async Task<ResultadoRecebimentoMvp> ExecutarAsync(RecebimentoSapCpiMvp entrada, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        var snapshot = await registro.RegistrarAsync(entrada, cancellationToken);
        return new ResultadoRecebimentoMvp(entrada.DocumentoLogisticoReferencia,
            snapshot.Correlacao.ProtocoloGuardian, true);
    }
}
