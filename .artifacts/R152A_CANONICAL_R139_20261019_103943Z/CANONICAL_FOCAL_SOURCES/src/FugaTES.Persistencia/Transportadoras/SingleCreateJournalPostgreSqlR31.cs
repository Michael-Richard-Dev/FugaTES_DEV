using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
using Npgsql;
using System.Data;
using System.Data.Common;
namespace FugaTES.Persistencia.Transportadoras;

// Journal durable do CREATE unitario de transportadora (R31).
// Garante o que o caso de uso exige: rejeitar atomicamente escrita pendente ou replay para o
// mesmo bp/codigo, e confirmar de forma durable um CREATE ja aceito pelo Guardian.
// Append/confirm apenas: nenhuma sentenca DELETE ou TRUNCATE, coerente com o modelo de
// privilegios do papel de aplicacao (SELECT/INSERT/UPDATE, sem DELETE/TRUNCATE).
public sealed class SingleCreateJournalPostgreSqlR31(DbDataSource source, TimeProvider? clock = null) : ISingleCreateJournalR31
{
    public const string Table = "fugates.carrier_single_create_journal";

    // Reserva atomica em UMA sentenca. O predicado NOT EXISTS bloqueia pendencia e replay visiveis;
    // a corrida concorrente que o predicado nao cobre sob READ COMMITTED e resolvida pelos indices
    // unicos de bp, guardian_code e attempt_id, cuja violacao e classificada como rejeicao definida.
    public const string PrepareSql = """
        insert into fugates.carrier_single_create_journal(attempt_id,bp,guardian_code,payload_hash,status,prepared_utc)
        select @attempt,@bp,@code,@hash,'PREPARED',@at
        where not exists(
            select 1 from fugates.carrier_single_create_journal
            where bp=@bp or guardian_code=@code or attempt_id=@attempt)
        """;

    public const string ConfirmSql = """
        update fugates.carrier_single_create_journal set status='CONFIRMED',confirmed_utc=@at
        where attempt_id=@attempt and status='PREPARED'
        """;

    public const string ObserveSql = "select status from fugates.carrier_single_create_journal where attempt_id=@attempt";

    public async Task<bool> TryPrepareAsync(Guid attemptId, string bp, string code, string payloadHash, CancellationToken ct)
    {
        SingleCreateJournalPolicyR31.ValidateEntry(attemptId, bp, code, payloadHash);
        var at = (clock ?? TimeProvider.System).GetUtcNow().ToUniversalTime();
        try
        {
            // A dedicated non-enlisted Npgsql source is supplied by the R139 desktop binding.
            // DbDataSource is the standard ADO.NET seam for offline failure/ordering tests.
            await using var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = PrepareSql;
            Add(command,"attempt", attemptId);
            Add(command,"bp", bp);
            Add(command,"code", code);
            Add(command,"hash", payloadHash);
            Add(command,"at", at);
            var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            var outcome = SingleCreateJournalPolicyR31.ClassifyInsert(affected);
            if (outcome != SingleCreatePrepareOutcomeR31.Reserved)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                if (outcome == SingleCreatePrepareOutcomeR31.Rejected) return false;
                throw new InvalidOperationException("SINGLE_CREATE_PREPARE_ROWS_UNKNOWN");
            }
            // No successful return until this commit acknowledgement. A commit exception
            // propagates to CREATE_PREPARE_OUTCOME_UNKNOWN, never to a Guardian dispatch.
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException failure)
            when (SingleCreateJournalPolicyR31.ClassifyFailure(failure.SqlState) == SingleCreatePrepareOutcomeR31.Rejected)
        {
            // Reserva perdida para outra tentativa. Nada foi enviado por esta: rejeicao definida,
            // nunca indeterminado, para que o caso de uso bloqueie em vez de reenviar.
            return false;
        }
        // Qualquer outra falha propaga e o caso de uso a trata como resultado indeterminado.
    }

    public async Task ConfirmAsync(Guid attemptId, CancellationToken ct)
    {
        if (attemptId == Guid.Empty) throw new ArgumentException("SINGLE_CREATE_JOURNAL_ATTEMPT_INVALID");
        var at = (clock ?? TimeProvider.System).GetUtcNow().ToUniversalTime();
        await using var command = source.CreateCommand(ConfirmSql);
        Add(command,"attempt", attemptId);
        Add(command,"at", at);
        var affected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected == 1) return;
        // Zero linha pode ser reconfirmacao idempotente do mesmo attempt; so o estado observado decide.
        await using var observe = source.CreateCommand(ObserveSql);
        Add(observe,"attempt", attemptId);
        var status = await observe.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (!SingleCreateJournalPolicyR31.ConfirmSatisfied(affected, status))
            throw new InvalidOperationException("SINGLE_CREATE_JOURNAL_CONFIRM_NOT_PERSISTED");
    }
    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
