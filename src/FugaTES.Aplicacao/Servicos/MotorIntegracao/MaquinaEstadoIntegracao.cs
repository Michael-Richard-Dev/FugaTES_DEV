using FugaTES.Aplicacao.Modelos.MotorIntegracao;

namespace FugaTES.Aplicacao.Servicos.MotorIntegracao;

public static class MaquinaEstadoIntegracao
{
    public static bool PodeClaimar(EstadoIntegracao estado)
    {
        return estado is EstadoIntegracao.Pendente or EstadoIntegracao.AguardandoReprocessamento;
    }

    public static bool PodeAplicarTipoDisparo(EstadoIntegracao estado, TipoDisparoPendente? tipo)
    {
        return estado switch
        {
            EstadoIntegracao.Pendente => tipo is TipoDisparoPendente.Automatico or TipoDisparoPendente.IntegrarAgora,
            EstadoIntegracao.AguardandoReprocessamento => tipo is TipoDisparoPendente.Automatico or TipoDisparoPendente.Reprocessamento,
            _ => tipo is null
        };
    }

    public static EstadoIntegracao AplicarResultado(ClassificacaoFalhaIntegracao? falha)
    {
        return falha switch
        {
            null => EstadoIntegracao.Concluida,
            ClassificacaoFalhaIntegracao.FalhaInternaPreEnvio => EstadoIntegracao.AguardandoReprocessamento,
            ClassificacaoFalhaIntegracao.FalhaTransitoriaSegura => EstadoIntegracao.AguardandoReprocessamento,
            ClassificacaoFalhaIntegracao.ResultadoIndeterminado => EstadoIntegracao.ResultadoIndeterminado,
            ClassificacaoFalhaIntegracao.FalhaDefinitiva => EstadoIntegracao.FalhaDefinitiva,
            _ => throw new ArgumentOutOfRangeException(nameof(falha), falha, "Classificacao de falha nao reconhecida.")
        };
    }

    public static bool PodeReprocessar(EstadoIntegracao estado)
    {
        return estado == EstadoIntegracao.AguardandoAcaoManual;
    }
}
