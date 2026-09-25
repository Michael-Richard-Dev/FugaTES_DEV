using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Modelos.Recebimento;

public class FalhaPesagemSapMvpException(
    string message, ClassificacaoFalhaIntegracao classificacao, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ClassificacaoFalhaIntegracao Classificacao { get; } = classificacao;
}
