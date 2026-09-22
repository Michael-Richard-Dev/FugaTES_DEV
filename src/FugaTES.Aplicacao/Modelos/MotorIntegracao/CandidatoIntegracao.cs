namespace FugaTES.Aplicacao.Modelos.MotorIntegracao;

// Candidato de integracao descoberto por um detector (S1 aditivo).
// O fingerprint ja vem calculado pelo detector; o motor nao recalcula formula alguma.
public sealed record CandidatoIntegracao(
    string Origem,
    string Destino,
    string Operacao,
    string ChaveNegocio,
    string FingerprintSha256,
    string CorrelationId);
