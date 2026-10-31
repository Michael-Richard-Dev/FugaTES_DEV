using System.Data;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using Npgsql;
using NpgsqlTypes;

namespace FugaTES.Persistencia.ContratosSapToledo;

// Exclusivamente as colunas adicionais da tabela H4; NUNCA usa tabelas de callback H7.
public sealed class AcmGuardianVinculoPostgreSql(NpgsqlDataSource dataSource) : IAcmGuardianVinculoStore
{
    private const string Fields = "numero_contrato,versao,integracao_id,guardian_numero_referencia," +
        "guardian_codigo_solicitado,guardian_codigo_confirmado,guardian_protocolo_id," +
        "guardian_numero_ticket,guardian_callback_id,guardian_callback_sequencial,guardian_callback_fingerprint";

    public Task<AcmGuardianVinculo> ReservarAsync(VersaoContratoSapToledo versao,string numeroReferencia,string codigoContratoSolicitado,CancellationToken ct)
        => ReservarCoreAsync(versao,numeroReferencia,codigoContratoSolicitado,ct,false);
    public Task<AcmGuardianVinculo> ReservarSolicitacaoUnicaAsync(VersaoContratoSapToledo versao,string referencia,string codigo,CancellationToken ct)
        => ReservarCoreAsync(versao,referencia,codigo,ct,true);
    private async Task<AcmGuardianVinculo> ReservarCoreAsync(VersaoContratoSapToledo versao,string numeroReferencia,string codigoContratoSolicitado,CancellationToken ct,bool unica)
    {
        ArgumentNullException.ThrowIfNull(versao);
        ExigirTexto(numeroReferencia); ExigirTexto(codigoContratoSolicitado);
        if (versao.IntegracaoId == Guid.Empty || versao.Versao <= 0 ||
            versao.NumeroContrato != versao.Snapshot.NumeroContrato)
            throw new InvalidOperationException("H4_SOURCE_IDENTITY_INVALID");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // Lock pela referencia, para impedir colisoes por retirada de zeros / concorrencia.
            await using (var advisory = new NpgsqlCommand(
                "select pg_advisory_xact_lock(hashtextextended(@referencia,0));", conn, tx))
            {
                advisory.Parameters.AddWithValue("referencia", numeroReferencia);
                await advisory.ExecuteNonQueryAsync(ct);
            }
            await using (var collision = new NpgsqlCommand("""
                select exists(select 1 from fugates.contrato_sap_toledo_versao
                  where guardian_numero_referencia = @referencia and (numero_contrato <> @numero or (@unica and integracao_id <> @id)));
                """, conn, tx))
            {
                collision.Parameters.AddWithValue("referencia", numeroReferencia);
                collision.Parameters.AddWithValue("numero", versao.NumeroContrato);
                collision.Parameters.AddWithValue("unica",unica);collision.Parameters.AddWithValue("id",versao.IntegracaoId);
                if (await collision.ExecuteScalarAsync(ct) is true)
                    throw new InvalidOperationException("ACM_REFERENCE_COLLISION_DIFFERENT_SAP_IDENTITY");
            }
            var sql = $"""
                update fugates.contrato_sap_toledo_versao
                set guardian_numero_referencia = @referencia,
                    guardian_codigo_solicitado = @codigo
                where integracao_id = @id and numero_contrato = @numero and versao = @versao
                  and ((guardian_numero_referencia is null and guardian_codigo_solicitado is null)
                    or (guardian_numero_referencia = @referencia and guardian_codigo_solicitado = @codigo))
                returning {Fields};
                """;
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("referencia", numeroReferencia);
            cmd.Parameters.AddWithValue("codigo", codigoContratoSolicitado);
            cmd.Parameters.AddWithValue("id", versao.IntegracaoId);
            cmd.Parameters.AddWithValue("numero", versao.NumeroContrato);
            cmd.Parameters.AddWithValue("versao", versao.Versao);
            var result = await OneAsync(cmd, ct) ?? throw new InvalidOperationException("ACM_H4_RESERVATION_CONFLICT");
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<AcmGuardianVinculo?> ObterPorIntegracaoAsync(Guid integracaoId, CancellationToken ct)
    {
        var sql = $"select {Fields} from fugates.contrato_sap_toledo_versao where integracao_id=@id and guardian_numero_referencia is not null;";
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", integracaoId);
        return await OneAsync(cmd, ct);
    }

    public Task<AcmGuardianVinculo> RegistrarCodigoConfirmadoAsync(Guid integracaoId, string codigo, CancellationToken ct)
    {
        ExigirTexto(codigo);
        return AtualizarAsync(integracaoId, """
            guardian_codigo_confirmado = @codigo
            """, "guardian_codigo_solicitado = @codigo and (guardian_codigo_confirmado is null or guardian_codigo_confirmado=@codigo)",
            cmd => cmd.Parameters.AddWithValue("codigo", codigo), ct);
    }

    public Task<AcmGuardianVinculo> RegistrarProtocoloConfirmadoAsync(Guid integracaoId,
        long protocoloId, string? numeroTicket, CancellationToken ct)
    {
        if(numeroTicket is not null)ExigirTexto(numeroTicket);
        if (protocoloId <= 0) throw new InvalidOperationException("ACM_GUARDIAN_PROTOCOL_INVALID");
        return AtualizarAsync(integracaoId,
            "guardian_protocolo_id=@protocolo,guardian_numero_ticket=@ticket",
            "guardian_codigo_confirmado is not null and (guardian_protocolo_id is null or guardian_protocolo_id=@protocolo) " +
            "and (guardian_numero_ticket is null or guardian_numero_ticket=@ticket)",
            cmd => { cmd.Parameters.AddWithValue("protocolo", protocoloId); cmd.Parameters.AddWithValue("ticket",NpgsqlDbType.Text,(object?)numeroTicket ?? DBNull.Value); }, ct);
    }

    public Task<AcmGuardianVinculo> VincularCallbackComprovadoAsync(Guid integracaoId,
        long callbackId, long callbackSequencial, CancellationToken ct)
    {
        if (callbackId <= 0 || callbackSequencial <= 0)
            throw new InvalidOperationException("ACM_CALLBACK_IDENTITY_INVALID");
        return AtualizarAsync(integracaoId,
            "guardian_callback_id=@callback,guardian_callback_sequencial=@sequencial",
            "guardian_protocolo_id is not null and (guardian_callback_id is null or guardian_callback_id=@callback) " +
            "and (guardian_callback_sequencial is null or guardian_callback_sequencial=@sequencial)",
            cmd => { cmd.Parameters.AddWithValue("callback", callbackId); cmd.Parameters.AddWithValue("sequencial", callbackSequencial); }, ct);
    }

    public async Task<AcmGuardianVinculo?> ResolverCallbackAsync(long callbackId, long callbackSequencial, CancellationToken ct)
    {
        if (callbackId <= 0 || callbackSequencial <= 0) return null;
        var sql = $"""
            select {Fields} from fugates.contrato_sap_toledo_versao
            where guardian_callback_id=@callback and guardian_callback_sequencial=@sequencial
              and guardian_codigo_confirmado is not null and guardian_protocolo_id is not null
              and guardian_numero_ticket is not null;
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("callback", callbackId);
        cmd.Parameters.AddWithValue("sequencial", callbackSequencial);
        return await OneAsync(cmd, ct);
    }

    public async Task<StatusEventoAcmGuardian> RegistrarFingerprintCallbackAsync(
        long callbackId, long callbackSequencial, string fingerprintSha256, CancellationToken ct)
    {
        if (callbackId <= 0 || callbackSequencial <= 0 || fingerprintSha256.Length != 64 ||
            fingerprintSha256.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidOperationException("ACM_CALLBACK_FINGERPRINT_INVALID");
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using var cmd = new NpgsqlCommand("""
                select guardian_callback_fingerprint from fugates.contrato_sap_toledo_versao
                where guardian_callback_id=@callback and guardian_callback_sequencial=@sequencial
                  and guardian_codigo_confirmado is not null and guardian_protocolo_id is not null
                for update;
                """, conn, tx);
            cmd.Parameters.AddWithValue("callback", callbackId);
            cmd.Parameters.AddWithValue("sequencial", callbackSequencial);
            string? previous;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("ACM_CALLBACK_CORRELATION_NOT_PROVEN");
                previous = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (await reader.ReadAsync(ct)) throw new InvalidOperationException("ACM_CALLBACK_AMBIGUOUS");
            }
            if (previous is not null && previous != fingerprintSha256)
                throw new InvalidOperationException("ACM_CALLBACK_PAYLOAD_CONFLICT");
            if (previous is null)
            {
                await using var update = new NpgsqlCommand("""
                    update fugates.contrato_sap_toledo_versao set guardian_callback_fingerprint=@fp
                    where guardian_callback_id=@callback and guardian_callback_sequencial=@sequencial
                      and guardian_callback_fingerprint is null;
                    """, conn, tx);
                update.Parameters.AddWithValue("callback", callbackId);
                update.Parameters.AddWithValue("sequencial", callbackSequencial);
                update.Parameters.AddWithValue("fp", fingerprintSha256);
                if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("ACM_CALLBACK_LEDGER_WRITE_FAILED");
            }
            await tx.CommitAsync(ct);
            return previous is null ? StatusEventoAcmGuardian.Novo : StatusEventoAcmGuardian.Idempotente;
        }
        catch { await tx.RollbackAsync(CancellationToken.None); throw; }
    }

    private async Task<AcmGuardianVinculo> AtualizarAsync(Guid id, string setSql, string conditionSql,
        Action<NpgsqlCommand> parameters, CancellationToken ct)
    {
        var sql = $"""
            update fugates.contrato_sap_toledo_versao set {setSql}
            where integracao_id=@id and guardian_numero_referencia is not null and {conditionSql}
            returning {Fields};
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        parameters(cmd);
        try { return await OneAsync(cmd, ct) ?? throw new InvalidOperationException("ACM_GUARDIAN_IDENTITY_CONFLICT_OR_MISSING"); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        { throw new InvalidOperationException("ACM_GUARDIAN_IDENTITY_COLLISION", ex); }
    }

    private static async Task<AcmGuardianVinculo?> OneAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        long? Id(int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
        var result = new AcmGuardianVinculo(reader.GetString(0), reader.GetInt64(1), reader.GetGuid(2),
            Text(3)!, Text(4)!, Text(5), Id(6), Text(7), Id(8), Id(9), Text(10));
        if (await reader.ReadAsync(ct)) throw new InvalidOperationException("ACM_GUARDIAN_AMBIGUOUS_LINK");
        return result;
    }
    private static void ExigirTexto(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim())
            throw new InvalidOperationException("ACM_GUARDIAN_TEXT_INVALID");
    }
}
