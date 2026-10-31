using System.Data;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;

namespace FugaTES.Persistencia.Recebimento;

public sealed partial class RepositorioCorrelacaoGuardianSapPostgreSql(NpgsqlDataSource dataSource) : IRepositorioCorrelacaoRecebimento, IRegistroCallbackPesagemSapStore, IAcmSapTicketEvidenceStore
{
    private const string InsertSnapshotSql = """
        insert into fugates.correlacao_guardian_sap
            (id, documento_logistico_referencia, material_sap, bp_emissor, bp_transportadora, centro_sap, tipo_documento, contrato_sap, operacao_contrato, tipo_operacao_sap, pedido_compras_sap, tipo_contrato, url_para_retorno, processo, direcao, estado, criado_em, atualizado_em)
        values (@id, @documento_logistico_referencia, @material_sap, @bp_emissor, @bp_transportadora, @centro_sap, @tipo_documento, @contrato_sap, @operacao_contrato, @tipo_operacao_sap, @pedido_compras_sap, @tipo_contrato, @url_para_retorno, 'RECEBIMENTO', 'SAP_PARA_GUARDIAN', 'AGUARDANDO_PROTOCOLO', now(), now())
        on conflict (documento_logistico_referencia) do nothing returning id;
        """;
    private const string SelectByDocumentSql = "select * from fugates.correlacao_guardian_sap where documento_logistico_referencia = @documento_logistico_referencia;";
    private const string SelectByProtocolSql = "select * from fugates.correlacao_guardian_sap where protocolo_guardian = @protocolo_guardian;";
    private const string SelectByProtocolForUpdateSql = "select * from fugates.correlacao_guardian_sap where protocolo_guardian = @protocolo_guardian for update;";
    private const string AssociateProtocolSql = """
        update fugates.correlacao_guardian_sap set protocolo_guardian = @protocolo_guardian, protocolo_criado_em = @protocolo_criado_em, estado = 'AGUARDANDO_TICKET', atualizado_em = now()
        where documento_logistico_referencia = @documento_logistico_referencia and protocolo_guardian is null and protocolo_criado_em is null returning id;
        """;
    private const string AssociateContractAcmSql = """
        update fugates.correlacao_guardian_sap set contrato_acm_sap = @contrato_acm_sap, atualizado_em = now()
        where documento_logistico_referencia = @documento_logistico_referencia and contrato_acm_sap is null returning id;
        """;
    private const string RegisterCallbackSql = """
        update fugates.correlacao_guardian_sap set ticket_guardian = @ticket_guardian, callback_fingerprint = @callback_fingerprint, estado = 'TICKET_ASSOCIADO', atualizado_em = now()
        where protocolo_guardian = @protocolo_guardian and callback_fingerprint is null returning id;
        """;
    private const string RegisterCallbackWorkSql = """
        update fugates.correlacao_guardian_sap set ticket_guardian = @ticket_guardian,
            callback_fingerprint = @callback_fingerprint, peso_bruto = @peso_bruto,
            tara_final = @tara_final, peso_liquido = @peso_liquido,
            estado = 'TICKET_ASSOCIADO', atualizado_em = now()
        where protocolo_guardian = @protocolo_guardian and callback_fingerprint is null returning id;
        """;
    private const string CheckIntegrationSql = """
        select exists(select 1 from integracao where fingerprint_sha256 = @fingerprint_sha256
            and operacao = @operacao and chave_negocio = @chave_negocio);
        """;

    public async Task<ResultadoRegistroCallbackMvp> RegistrarEEnfileirarAsync(
        CallbackGuardianMvp callback, CandidatoIntegracao candidato, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(candidato);
        var fingerprint = CallbackGuardianMvpFingerprint.Calcular(callback);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var locked = new NpgsqlCommand(SelectByProtocolForUpdateSql, connection, transaction);
            locked.Parameters.AddWithValue("protocolo_guardian", callback.ProtocoloGuardian);
            CorrelacaoRecebimentoMvp atual;
            await using (var reader = await locked.ExecuteReaderAsync(cancellationToken))
                atual = await reader.ReadAsync(cancellationToken) ? Map(reader) : throw new InvalidOperationException("Guardian callback correlation was not found.");

            if (atual.CallbackFingerprint is not null)
            {
                var pesos = new PesosCallbackSapMvp(callback.PesoBruto, callback.Tara, callback.PesoLiquido);
                var same = atual.TicketGuardian == callback.Sequencial.Trim() &&
                    atual.CallbackFingerprint == fingerprint && atual.Pesos == pesos;
                if (same)
                {
                    await using var check = new NpgsqlCommand(CheckIntegrationSql, connection, transaction);
                    check.Parameters.AddWithValue("fingerprint_sha256", candidato.FingerprintSha256);
                    check.Parameters.AddWithValue("operacao", candidato.Operacao);
                    check.Parameters.AddWithValue("chave_negocio", candidato.ChaveNegocio);
                    if (await check.ExecuteScalarAsync(cancellationToken) is not true)
                        throw new InvalidOperationException("Accepted callback has no registered integration.");
                }
                await transaction.CommitAsync(cancellationToken);
                return new(same ? StatusRegistroCallbackMvp.Idempotente : StatusRegistroCallbackMvp.Conflito, atual);
            }

            await using var update = new NpgsqlCommand(RegisterCallbackWorkSql, connection, transaction);
            update.Parameters.AddWithValue("protocolo_guardian", callback.ProtocoloGuardian);
            update.Parameters.AddWithValue("ticket_guardian", callback.Sequencial.Trim());
            update.Parameters.AddWithValue("callback_fingerprint", fingerprint);
            update.Parameters.AddWithValue("peso_bruto", callback.PesoBruto);
            update.Parameters.AddWithValue("tara_final", callback.Tara);
            update.Parameters.AddWithValue("peso_liquido", callback.PesoLiquido);
            if (await update.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("Callback snapshot could not be registered.");
            var result = await RepositorioMotorIntegracaoPostgreSql.RegistrarCandidatoNaTransacaoAsync(
                connection, transaction, candidato, Guid.NewGuid(), cancellationToken);
            if (result != ResultadoRegistroIntegracao.Criada)
                throw new InvalidOperationException("Callback integration identity already exists.");
            await transaction.CommitAsync(cancellationToken);
            return new(StatusRegistroCallbackMvp.Novo, atual with {
                TicketGuardian = callback.Sequencial.Trim(), CallbackFingerprint = fingerprint,
                Pesos = new(callback.PesoBruto, callback.Tara, callback.PesoLiquido), Callback = callback,
                Estado = EstadoCorrelacaoGuardianSap.TicketAssociado });
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException("Callback identity conflicts with persisted work.", exception);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp entrada, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var insert = new NpgsqlCommand(InsertSnapshotSql, connection, transaction);
        AddSnapshot(insert, entrada);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken) is not null;
        var atual = await SelectByDocumentAsync(connection, transaction, entrada.DocumentoLogisticoReferencia, cancellationToken) ?? throw new InvalidOperationException("Receipt snapshot could not be loaded.");
        await transaction.CommitAsync(cancellationToken);
        return new(inserted ? StatusRegistroSnapshotMvp.Criada : SnapshotIgual(atual.Entrada, entrada) ? StatusRegistroSnapshotMvp.Idempotente : StatusRegistroSnapshotMvp.Conflito, atual);
    }

    public async Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string documentoSap, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await ObterPorDocumentoAsync(connection, documentoSap, cancellationToken);
    }

    public async Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string documentoSap, string protocoloGuardian, CancellationToken cancellationToken, DateTimeOffset protocoloCriadoEm)
    {
        if (protocoloCriadoEm == default) throw new ArgumentException("Protocol creation timestamp is required.", nameof(protocoloCriadoEm));
        var instante = new DateTimeOffset(protocoloCriadoEm.ToUniversalTime().Ticks / 10 * 10, TimeSpan.Zero);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var update = new NpgsqlCommand(AssociateProtocolSql, connection, transaction);
            update.Parameters.AddWithValue("documento_logistico_referencia", documentoSap);
            update.Parameters.AddWithValue("protocolo_guardian", protocoloGuardian);
            update.Parameters.AddWithValue("protocolo_criado_em", NpgsqlTypes.NpgsqlDbType.TimestampTz, instante);
            var associated = await update.ExecuteScalarAsync(cancellationToken) is not null;
            var atual = await SelectByDocumentAsync(connection, transaction, documentoSap, cancellationToken) ?? throw new InvalidOperationException("Receipt snapshot was not found.");
            await transaction.CommitAsync(cancellationToken);
            return new(associated ? StatusAssociacaoProtocoloMvp.Associado : atual.ProtocoloGuardian == protocoloGuardian && atual.ProtocoloCriadoEm == instante ? StatusAssociacaoProtocoloMvp.Idempotente : StatusAssociacaoProtocoloMvp.Conflito, atual);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            var atual = await ObterPorDocumentoAsync(connection, documentoSap, cancellationToken) ?? throw new InvalidOperationException("Receipt snapshot was not found.");
            return new(StatusAssociacaoProtocoloMvp.Conflito, atual);
        }
    }

    public async Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string documentoSap, string contratoAcmSap, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contratoAcmSap)) throw new ArgumentException("ACM contract is absent.", nameof(contratoAcmSap));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var update = new NpgsqlCommand(AssociateContractAcmSql, connection, transaction);
        update.Parameters.AddWithValue("documento_logistico_referencia", documentoSap);
        update.Parameters.AddWithValue("contrato_acm_sap", contratoAcmSap);
        var associated = await update.ExecuteScalarAsync(cancellationToken) is not null;
        var atual = await SelectByDocumentAsync(connection, transaction, documentoSap, cancellationToken) ?? throw new InvalidOperationException("Receipt snapshot was not found.");
        await transaction.CommitAsync(cancellationToken);
        return new(associated ? StatusAssociacaoContratoAcmMvp.Associado : atual.Entrada.ContratoAcmSap == contratoAcmSap ? StatusAssociacaoContratoAcmMvp.Idempotente : StatusAssociacaoContratoAcmMvp.Conflito, atual);
    }

    public async Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocoloGuardian, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SelectByProtocolSql, connection);
        command.Parameters.AddWithValue("protocolo_guardian", protocoloGuardian);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken cancellationToken)
    {
        var fingerprint = CallbackGuardianMvpFingerprint.Calcular(callback);
        var ticket = callback.Sequencial.Trim();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var update = new NpgsqlCommand(RegisterCallbackSql, connection, transaction);
            update.Parameters.AddWithValue("protocolo_guardian", callback.ProtocoloGuardian);
            update.Parameters.AddWithValue("ticket_guardian", ticket);
            update.Parameters.AddWithValue("callback_fingerprint", fingerprint);
            var registered = await update.ExecuteScalarAsync(cancellationToken) is not null;
            var atual = await SelectByProtocolAsync(connection, transaction, callback.ProtocoloGuardian, cancellationToken) ?? throw new InvalidOperationException("Guardian callback correlation was not found.");
            await transaction.CommitAsync(cancellationToken);
            var status = registered ? StatusRegistroCallbackMvp.Novo : atual.TicketGuardian == ticket && atual.CallbackFingerprint == fingerprint ? StatusRegistroCallbackMvp.Idempotente : StatusRegistroCallbackMvp.Conflito;
            return new(status, registered ? atual with { Callback = callback } : atual);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            var atual = await ObterPorProtocoloAsync(callback.ProtocoloGuardian, cancellationToken) ?? throw new InvalidOperationException("Guardian callback correlation was not found.");
            return new(StatusRegistroCallbackMvp.Conflito, atual);
        }
    }

    private static async Task<CorrelacaoRecebimentoMvp?> SelectByDocumentAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string document, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(SelectByDocumentSql, connection, transaction);
        command.Parameters.AddWithValue("documento_logistico_referencia", document);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static async Task<CorrelacaoRecebimentoMvp?> SelectByProtocolAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string protocol, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(SelectByProtocolSql, connection, transaction);
        command.Parameters.AddWithValue("protocolo_guardian", protocol);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static async Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(NpgsqlConnection connection, string document, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(SelectByDocumentSql, connection);
        command.Parameters.AddWithValue("documento_logistico_referencia", document);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Map(reader) : null;
    }

    private static CorrelacaoRecebimentoMvp Map(NpgsqlDataReader reader)
    {
        var document = reader.GetString(reader.GetOrdinal("documento_logistico_referencia"));
        var input = new RecebimentoSapCpiMvp(reader.GetString(reader.GetOrdinal("processo")), "FLX_RECEBIMENTO", reader.GetString(reader.GetOrdinal("material_sap")), reader.GetString(reader.GetOrdinal("bp_emissor")), reader.GetString(reader.GetOrdinal("bp_transportadora")), reader.GetString(reader.GetOrdinal("contrato_sap")), document, NullableString(reader, "tipo_contrato") ?? string.Empty, reader.GetString(reader.GetOrdinal("centro_sap")), reader.GetString(reader.GetOrdinal("operacao_contrato")), reader.GetString(reader.GetOrdinal("tipo_documento")), NullableString(reader, "pedido_compras_sap"), reader.GetInt16(reader.GetOrdinal("tipo_operacao_sap")), NullableString(reader, "contrato_acm_sap"), NullableString(reader, "url_para_retorno"));
        var createdOrdinal = reader.GetOrdinal("protocolo_criado_em");
        var created = reader.IsDBNull(createdOrdinal) ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(createdOrdinal), DateTimeKind.Utc));
        var pesoOrdinal = reader.GetOrdinal("peso_bruto");
        var pesos = reader.IsDBNull(pesoOrdinal) ? null : new PesosCallbackSapMvp(
            reader.GetDecimal(pesoOrdinal), reader.GetDecimal(reader.GetOrdinal("tara_final")),
            reader.GetDecimal(reader.GetOrdinal("peso_liquido")));
        return new(document, NullableString(reader, "protocolo_guardian"), input, NullableString(reader, "ticket_guardian"), null, ParseState(reader.GetString(reader.GetOrdinal("estado"))), NullableString(reader, "callback_fingerprint"), created, pesos);
    }

    private static string? NullableString(NpgsqlDataReader reader, string name) { var ordinal = reader.GetOrdinal(name); return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal); }
    private static EstadoCorrelacaoGuardianSap ParseState(string state) => state switch { "AGUARDANDO_PROTOCOLO" => EstadoCorrelacaoGuardianSap.AguardandoProtocolo, "AGUARDANDO_TICKET" => EstadoCorrelacaoGuardianSap.AguardandoTicket, "TICKET_ASSOCIADO" => EstadoCorrelacaoGuardianSap.TicketAssociado, _ => throw new InvalidOperationException("Unknown correlation state.") };
    private static void AddSnapshot(NpgsqlCommand command, RecebimentoSapCpiMvp input)
    {
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("documento_logistico_referencia", input.DocumentoLogisticoReferencia);
        command.Parameters.AddWithValue("material_sap", input.Material); command.Parameters.AddWithValue("bp_emissor", input.BusinessPartnerEmissor);
        command.Parameters.AddWithValue("bp_transportadora", input.BusinessPartnerTransportadora); command.Parameters.AddWithValue("centro_sap", input.Centro);
        command.Parameters.AddWithValue("tipo_documento", input.TipoDocumento); command.Parameters.AddWithValue("contrato_sap", input.Contrato);
        command.Parameters.AddWithValue("operacao_contrato", input.OperacaoContrato); command.Parameters.AddWithValue("tipo_operacao_sap", input.TipoOperacaoSap);
        command.Parameters.AddWithValue("pedido_compras_sap", NpgsqlTypes.NpgsqlDbType.Text, (object?)input.PedidoCompras ?? DBNull.Value);
        command.Parameters.AddWithValue("tipo_contrato", NpgsqlTypes.NpgsqlDbType.Text, (object?)input.TipoContrato ?? DBNull.Value);
        command.Parameters.AddWithValue("url_para_retorno", NpgsqlTypes.NpgsqlDbType.Text, (object?)input.UrlParaRetorno ?? DBNull.Value);
    }
    private static bool SnapshotIgual(RecebimentoSapCpiMvp left, RecebimentoSapCpiMvp right) => left.DocumentoLogisticoReferencia == right.DocumentoLogisticoReferencia && left.Material == right.Material && left.BusinessPartnerEmissor == right.BusinessPartnerEmissor && left.BusinessPartnerTransportadora == right.BusinessPartnerTransportadora && left.Centro == right.Centro && left.TipoDocumento == right.TipoDocumento && left.Contrato == right.Contrato && left.OperacaoContrato == right.OperacaoContrato && left.TipoOperacaoSap == right.TipoOperacaoSap && left.PedidoCompras == right.PedidoCompras && left.TipoContrato == right.TipoContrato && left.UrlParaRetorno == right.UrlParaRetorno;
}
