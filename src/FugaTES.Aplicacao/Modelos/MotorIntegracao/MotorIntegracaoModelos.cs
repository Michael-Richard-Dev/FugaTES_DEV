namespace FugaTES.Aplicacao.Modelos.MotorIntegracao;

public enum EstadoIntegracao
{
    Pendente,
    EmProcessamento,
    AguardandoReprocessamento,
    AguardandoAcaoManual,
    ResultadoIndeterminado,
    Concluida,
    FalhaDefinitiva,
    Cancelada
}

public enum TipoDisparoPendente
{
    Automatico,
    IntegrarAgora,
    Reprocessamento
}

public enum FaseEnvioIntegracao
{
    NaoIniciado,
    EnvioIniciado,
    ResultadoRecebido
}

public enum ClassificacaoFalhaIntegracao
{
    FalhaInternaPreEnvio,
    FalhaTransitoriaSegura,
    FalhaDefinitiva,
    ResultadoIndeterminado
}

public enum EscopoPausaProcessamento
{
    Geral,
    SistemaExterno,
    Operacao
}

public sealed record IdentidadeWorkerIntegracao(string Valor);

public sealed record IntegracaoMotor(
    Guid Id,
    string Origem,
    string Destino,
    string Operacao,
    string ChaveNegocio,
    string FingerprintSha256,
    string CorrelationId,
    EstadoIntegracao Estado,
    TipoDisparoPendente? TipoDisparoPendente,
    DateTimeOffset? ProximaTentativaEm,
    long FencingToken,
    string? WorkerProprietario,
    DateTimeOffset? LeaseExpiraEm,
    FaseEnvioIntegracao FaseEnvio,
    Guid? TentativaAtualId);

public sealed record TentativaIntegracaoMotor(
    Guid Id,
    Guid IntegracaoId,
    long FencingToken,
    TipoDisparoPendente TipoDisparo,
    FaseEnvioIntegracao FaseEnvio,
    DesfechoTentativaIntegracao Desfecho,
    DateTimeOffset CriadaEm,
    DateTimeOffset? EnvioIniciadoEm,
    DateTimeOffset? ResultadoRecebidoEm);

public sealed record AuditoriaIntegracaoMotor(
    Guid Id,
    Guid? IntegracaoId,
    string Evento,
    DateTimeOffset CriadaEm,
    string? Worker,
    long? FencingToken,
    Guid? TentativaId,
    string? CorrelationId,
    string? Diagnostico = null);

public sealed record PausaProcessamentoMotor(
    Guid Id,
    EscopoPausaProcessamento Escopo,
    string? SistemaExterno,
    string? Operacao,
    bool Ativa,
    DateTimeOffset CriadaEm,
    DateTimeOffset? RetomadaEm);

public sealed record ClaimIntegracaoMotor(
    Guid IntegracaoId,
    Guid TentativaId,
    string CorrelationId,
    string Origem,
    string Destino,
    string Operacao,
    string ChaveNegocio,
    string FingerprintSha256,
    long FencingToken,
    string Worker,
    DateTimeOffset LeaseExpiraEm);

public enum ResultadoRegistroIntegracao
{
    Criada,
    JaExistente
}

public sealed record ResultadoProcessamentoExterno(ClassificacaoFalhaIntegracao? Falha)
{
    public static ResultadoProcessamentoExterno Sucesso { get; } = new((ClassificacaoFalhaIntegracao?)null);
}
