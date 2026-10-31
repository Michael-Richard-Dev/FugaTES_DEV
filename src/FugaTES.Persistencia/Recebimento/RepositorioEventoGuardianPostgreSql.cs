using System.Data;
using System.Globalization;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;
using NpgsqlTypes;

namespace FugaTES.Persistencia.Recebimento;

public sealed class RepositorioEventoGuardianPostgreSql(NpgsqlDataSource dataSource) : IRegistroEventoGuardianStore
{
    private const string LockParentSql = """
        select id, ticket_guardian, documento_logistico_referencia, callback_fingerprint,
               peso_bruto, tara_final, peso_liquido from fugates.correlacao_guardian_sap
        where protocolo_guardian = @protocolo_guardian for update;
        """;
    private const string SelectEventSql = """
        select callback_fingerprint, integracao_id from fugates.correlacao_guardian_evento
        where correlacao_id = @correlacao_id and sequencial_guardian = @sequencial_guardian
          and estado_codigo = @estado_codigo;
        """;
    private const string AttachTicketSql = """
        update fugates.correlacao_guardian_sap set ticket_guardian = @ticket_guardian, atualizado_em = now()
        where id = @correlacao_id and ticket_guardian is null;
        """;
    private const string InsertEventSql = """
        insert into fugates.correlacao_guardian_evento
            (id, correlacao_id, sequencial_guardian, estado_codigo, callback_fingerprint,
             peso_bruto, tara_final, peso_liquido, integracao_id, recebido_em)
        values (@id, @correlacao_id, @sequencial_guardian, @estado_codigo, @callback_fingerprint,
                @peso_bruto, @tara_final, @peso_liquido, @integracao_id, now());
        """;
    private const string ProjectWeighingSql = """
        update fugates.correlacao_guardian_sap set callback_fingerprint = @callback_fingerprint,
            peso_bruto = @peso_bruto, tara_final = @tara_final, peso_liquido = @peso_liquido,
            estado = 'TICKET_ASSOCIADO', atualizado_em = now()
        where id = @correlacao_id and callback_fingerprint is null returning id;
        """;
    private const string CheckIntegrationSql = """
        select exists(select 1 from integracao where id = @integracao_id
            and fingerprint_sha256 = @fingerprint_sha256 and operacao = @operacao
            and chave_negocio = @chave_negocio);
        """;

    public async Task<StatusRegistroEventoGuardian> RegistrarAsync(
        EventoGuardianRecebimento evento, string fingerprint, CancellationToken cancellationToken)
    {
        if (evento.Id <= 0 || evento.Sequencial <= 0 || evento.EstadoCodigo is not (1 or 3 or 5) ||
            (evento.EstadoCodigo == 5 &&
             (evento.PesoBruto is null || evento.Tara is null || evento.PesoLiquido is null)))
            throw new InvalidOperationException("Guardian event is missing its required weighing contract.");
        if (evento.EstadoCodigo == 5)
            GuardianFinalWeighingPolicy.RequireFinal(evento.PesoBruto!.Value, evento.Tara!.Value, evento.PesoLiquido!.Value);
        if (fingerprint != ProcessarEventoGuardianCasoDeUso.CalcularFingerprint(evento))
            throw new ArgumentException("GUARDIAN_EVENT_FINGERPRINT_INVALID");
        var protocol = evento.Id.ToString(CultureInfo.InvariantCulture);
        var ticket = evento.Sequencial.ToString(CultureInfo.InvariantCulture);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var locked = new NpgsqlCommand(LockParentSql, connection, transaction);
            locked.Parameters.AddWithValue("protocolo_guardian", protocol);
            Guid correlationId;
            string? currentTicket;
            string document;
            string? projectedFingerprint;
            decimal? projectedGross;
            decimal? projectedTare;
            decimal? projectedNet;
            await using (var reader = await locked.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    return StatusRegistroEventoGuardian.Conflito;
                correlationId = reader.GetGuid(0);
                currentTicket = reader.IsDBNull(1) ? null : reader.GetString(1);
                document = reader.GetString(2);
                projectedFingerprint = reader.IsDBNull(3) ? null : reader.GetString(3);
                projectedGross = reader.IsDBNull(4) ? null : reader.GetDecimal(4);
                projectedTare = reader.IsDBNull(5) ? null : reader.GetDecimal(5);
                projectedNet = reader.IsDBNull(6) ? null : reader.GetDecimal(6);
            }
            if (currentTicket is not null && currentTicket != ticket)
                return StatusRegistroEventoGuardian.Conflito;

            await using var select = new NpgsqlCommand(SelectEventSql, connection, transaction);
            select.Parameters.AddWithValue("correlacao_id", correlationId);
            select.Parameters.AddWithValue("sequencial_guardian", evento.Sequencial);
            select.Parameters.AddWithValue("estado_codigo", evento.EstadoCodigo);
            string? existing = null;
            Guid? existingIntegrationId = null;
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    existing = reader.GetString(0);
                    existingIntegrationId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                }
            }
            if (existing is not null)
            {
                if (existing != fingerprint)
                    return StatusRegistroEventoGuardian.Conflito;
                if (evento.EstadoCodigo == 5)
                {
                    if (existingIntegrationId is null || projectedFingerprint != fingerprint ||
                        projectedGross != evento.PesoBruto || projectedTare != evento.Tara ||
                        projectedNet != evento.PesoLiquido)
                        throw new InvalidOperationException("Accepted weighing event has incomplete persisted work.");
                    await using var check = new NpgsqlCommand(CheckIntegrationSql, connection, transaction);
                    check.Parameters.AddWithValue("integracao_id", existingIntegrationId.Value);
                    check.Parameters.AddWithValue("fingerprint_sha256", GuardianPesagemSapMotorWork.Fingerprint(protocol, fingerprint));
                    check.Parameters.AddWithValue("operacao", GuardianPesagemSapMotorWork.Operacao);
                    check.Parameters.AddWithValue("chave_negocio", protocol);
                    if (await check.ExecuteScalarAsync(cancellationToken) is not true)
                        throw new InvalidOperationException("Accepted weighing event has no matching integration.");
                }
                await transaction.CommitAsync(cancellationToken);
                return StatusRegistroEventoGuardian.Idempotente;
            }
            if (evento.EstadoCodigo == 5 && projectedFingerprint is not null)
                return StatusRegistroEventoGuardian.Conflito;
            var integrationId = evento.EstadoCodigo == 5 ? Guid.NewGuid() : (Guid?)null;
            if (currentTicket is null)
            {
                await using var attach = new NpgsqlCommand(AttachTicketSql, connection, transaction);
                attach.Parameters.AddWithValue("correlacao_id", correlationId);
                attach.Parameters.AddWithValue("ticket_guardian", ticket);
                if (await attach.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new InvalidOperationException("Ticket attach failed.");
            }
            await using var insert = new NpgsqlCommand(InsertEventSql, connection, transaction);
            var eventId = Guid.NewGuid();
            insert.Parameters.AddWithValue("id", eventId);
            insert.Parameters.AddWithValue("correlacao_id", correlationId);
            insert.Parameters.AddWithValue("sequencial_guardian", evento.Sequencial);
            insert.Parameters.AddWithValue("estado_codigo", evento.EstadoCodigo);
            insert.Parameters.AddWithValue("callback_fingerprint", fingerprint);
            insert.Parameters.AddWithValue("peso_bruto", NpgsqlDbType.Numeric, evento.EstadoCodigo == 5 ? evento.PesoBruto!.Value : DBNull.Value);
            insert.Parameters.AddWithValue("tara_final", NpgsqlDbType.Numeric, evento.EstadoCodigo == 5 ? evento.Tara!.Value : DBNull.Value);
            insert.Parameters.AddWithValue("peso_liquido", NpgsqlDbType.Numeric, evento.EstadoCodigo == 5 ? evento.PesoLiquido!.Value : DBNull.Value);
            insert.Parameters.AddWithValue("integracao_id", NpgsqlDbType.Uuid,
                integrationId.HasValue ? integrationId.Value : DBNull.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Guardian event ledger insert failed.");
            if (evento.EstadoCodigo == 5)
            {
                await using var project = new NpgsqlCommand(ProjectWeighingSql, connection, transaction);
                project.Parameters.AddWithValue("correlacao_id", correlationId);
                project.Parameters.AddWithValue("callback_fingerprint", fingerprint);
                project.Parameters.AddWithValue("peso_bruto", evento.PesoBruto!.Value);
                project.Parameters.AddWithValue("tara_final", evento.Tara!.Value);
                project.Parameters.AddWithValue("peso_liquido", evento.PesoLiquido!.Value);
                if (await project.ExecuteScalarAsync(cancellationToken) is null)
                    throw new InvalidOperationException("Guardian weighing projection failed.");

                var candidate = new CandidatoIntegracao("GUARDIAN", "SAP", GuardianPesagemSapMotorWork.Operacao,
                    protocol, GuardianPesagemSapMotorWork.Fingerprint(protocol, fingerprint), document);
                var registered = await RepositorioMotorIntegracaoPostgreSql.RegistrarCandidatoNaTransacaoAsync(
                    connection, transaction, candidate, integrationId!.Value, cancellationToken);
                if (registered != ResultadoRegistroIntegracao.Criada)
                    throw new InvalidOperationException("Guardian weighing integration identity already exists.");
            }
            await transaction.CommitAsync(cancellationToken);
            return StatusRegistroEventoGuardian.Novo;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return StatusRegistroEventoGuardian.Conflito;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
