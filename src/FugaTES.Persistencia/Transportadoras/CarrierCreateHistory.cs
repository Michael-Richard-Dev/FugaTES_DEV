using Npgsql;

namespace FugaTES.Persistencia.Transportadoras;

public sealed record CarrierCreateHistoryEntry(Guid AttemptId,string BusinessPartner,string GuardianCode,
    string Status,DateTimeOffset PreparedUtc,DateTimeOffset? ConfirmedUtc);

// Observes the original journal. No reconciliation writes or synthesized confirmations.
public sealed class CarrierCreateHistory(NpgsqlDataSource source)
{
    public const string Sql="""
        select attempt_id,bp,guardian_code,status,prepared_utc,confirmed_utc
        from fugates.carrier_single_create_journal
        where bp=@bp or guardian_code=@code order by prepared_utc desc limit 100
        """;
    public async Task<IReadOnlyList<CarrierCreateHistoryEntry>> ReadAsync(string bp,string code,CancellationToken ct)
    {
        await using var connection=await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction=await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
        await using(var ro=new NpgsqlCommand("set transaction read only; set local statement_timeout='5s'; set local lock_timeout='1s'",connection,transaction))await ro.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var command=new NpgsqlCommand(Sql,connection,transaction);
        command.Parameters.AddWithValue("bp",bp);command.Parameters.AddWithValue("code",code);
        await using var reader=await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows=new List<CarrierCreateHistoryEntry>();
        while(await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),reader.IsDBNull(5)?null:reader.GetFieldValue<DateTimeOffset>(5)));
        return rows;
        }
        finally{await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);}
    }
}
