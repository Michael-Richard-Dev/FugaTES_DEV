using Npgsql;

namespace FugaTES.HmlFixture;

internal sealed class PostgresFixtureReadback(NpgsqlDataSource dataSource) : IFixtureReadback
{
    public async Task<FixtureEvidence> ReadAsync(string protocol, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var parent = new NpgsqlCommand(
            "select id from fugates.correlacao_guardian_sap where protocolo_guardian = @protocol;", connection);
        parent.Parameters.AddWithValue("protocol", protocol);
        var id = await parent.ExecuteScalarAsync(cancellationToken);
        if (id is not Guid correlationId)
            throw new InvalidOperationException("HML parent diagnostic readback failed.");

        await using var ledger = new NpgsqlCommand(
            "select count(*) from fugates.correlacao_guardian_evento where correlacao_id = @correlation_id;", connection);
        ledger.Parameters.AddWithValue("correlation_id", correlationId);
        var eventCount = Convert.ToInt32(await ledger.ExecuteScalarAsync(cancellationToken));
        return new FixtureEvidence(correlationId, 1, eventCount);
    }
}
