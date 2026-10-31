namespace FugaTES.Aplicacao.Transportadoras.SingleCreateR31;

// Nucleo de decisao do journal de CREATE unitario. Puro: nenhuma conexao, driver ou I/O.
// O adapter PostgreSQL delega aqui para que replay, concorrencia perdida e confirmacao sejam
// verificaveis offline com a MESMA regra que roda em HML. Nenhuma semantica de negocio
// (estado, sequencial, perfil HML, equivalencia BP/codigo Guardian) e decidida neste arquivo.
public enum SingleCreatePrepareOutcomeR31 { Reserved, Rejected, Unknown }

public static class SingleCreateJournalPolicyR31
{
    public const string PreparedStatus = "PREPARED";
    public const string ConfirmedStatus = "CONFIRMED";

    // Violacao de unicidade/exclusao: outra tentativa JA ocupa este bp/codigo/attempt.
    // O resultado e DETERMINADO, nao indeterminado: esta tentativa nao enviou nada ao Guardian,
    // logo o caso de uso deve BLOQUEAR para reconciliacao em vez de tratar como envio incerto.
    public const string UniqueViolation = "23505";
    public const string ExclusionViolation = "23P01";

    public static void ValidateEntry(Guid attemptId, string bp, string guardianCode, string payloadHash)
    {
        if (attemptId == Guid.Empty) throw new ArgumentException("SINGLE_CREATE_JOURNAL_ATTEMPT_INVALID");
        CarrierPersistencePolicy.ValidateBp(bp);
        CarrierPersistencePolicy.ValidateCode(guardianCode);
        // Limite herdado do wire Guardian ja validado (codigo <= 20); nao e um limite novo inventado aqui.
        if (guardianCode.Length > 20) throw new ArgumentException("SINGLE_CREATE_JOURNAL_CODE_INVALID");
        CarrierPersistencePolicy.ValidateHash(payloadHash);
    }

    // Uma unica sentenca de reserva: 1 linha afetada reserva, 0 linha significa que o predicado
    // encontrou pendencia ou replay para este bp/codigo/attempt.
    public static SingleCreatePrepareOutcomeR31 ClassifyInsert(int affected) => affected switch
    {
        1 => SingleCreatePrepareOutcomeR31.Reserved,
        0 => SingleCreatePrepareOutcomeR31.Rejected,
        _ => SingleCreatePrepareOutcomeR31.Unknown
    };

    // Corrida concorrente que o predicado nao cobre sob READ COMMITTED e resolvida pelos indices
    // unicos do journal. Qualquer outro SQLSTATE permanece INDETERMINADO por principio.
    public static SingleCreatePrepareOutcomeR31 ClassifyFailure(string? sqlState) =>
        sqlState is UniqueViolation or ExclusionViolation
            ? SingleCreatePrepareOutcomeR31.Rejected
            : SingleCreatePrepareOutcomeR31.Unknown;

    // Confirmacao durable. Reconfirmacao do MESMO attempt ja confirmado e idempotente e nao
    // reabre envio; qualquer outro estado observado nega a confirmacao e preserva o indeterminado.
    public static bool ConfirmSatisfied(int affected, string? observedStatus) =>
        affected == 1 || (affected == 0 && observedStatus == ConfirmedStatus);
}
