namespace FugaTES.Aplicacao.Modelos.MotorIntegracao;

// Desfecho historico de uma tentativa de integracao (S1 aditivo).
// Semantica S1: apenas o contrato; a integracao ao pipeline/recovery ocorre nos proximos estagios.
public enum DesfechoTentativaIntegracao
{
    EmExecucao,
    Sucesso,
    Falha,
    Interrompida,
    Indeterminada
}
