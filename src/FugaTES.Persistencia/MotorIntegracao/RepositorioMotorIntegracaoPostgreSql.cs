using System.Data;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using Npgsql;

namespace FugaTES.Persistencia.MotorIntegracao;

public sealed class RepositorioMotorIntegracaoPostgreSql : IMotorIntegracaoRepository
{
    private readonly NpgsqlDataSource dataSource;

    public RepositorioMotorIntegracaoPostgreSql(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    public async Task<bool> EstaDisponivelAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select 1;");
        var resultado = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(resultado) == 1;
    }

    public async Task<bool> ExistePausaAplicavelAsync(string origem, string destino, string operacao, CancellationToken cancellationToken)
    {
        const string sql = """
            select exists (
                select 1
                from pausa_processamento p
                where p.ativa
                  and (
                    p.escopo = 'GERAL'
                    or (p.escopo = 'SISTEMA_EXTERNO' and p.sistema_externo in (@origem, @destino))
                    or (p.escopo = 'OPERACAO' and p.operacao = @operacao)
                  )
            );
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("origem", origem);
        command.Parameters.AddWithValue("destino", destino);
        command.Parameters.AddWithValue("operacao", operacao);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<ResultadoRegistroIntegracao> RegistrarCandidatoAsync(CandidatoIntegracao candidato, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidato);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var result = await RegistrarCandidatoNaTransacaoAsync(connection, transaction, candidato, Guid.NewGuid(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<ResultadoRegistroIntegracao> RegistrarCandidatoNaTransacaoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CandidatoIntegracao candidato, Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into integracao (
                id, origem, destino, operacao, chave_negocio, fingerprint_sha256, estado,
                tipo_disparo_pendente, proxima_tentativa_em, fencing_token, worker_proprietario,
                lease_expira_em, fase_envio, tentativa_atual_id, correlation_id, criado_em, atualizado_em)
            values (
                @id, @origem, @destino, @operacao, @chave_negocio, @fingerprint_sha256, 'PENDENTE',
                'AUTOMATICO', clock_timestamp(), 0, null, null, 'NAO_INICIADO', null, @correlation_id,
                clock_timestamp(), clock_timestamp())
            on conflict (fingerprint_sha256) do nothing;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("origem", candidato.Origem);
        command.Parameters.AddWithValue("destino", candidato.Destino);
        command.Parameters.AddWithValue("operacao", candidato.Operacao);
        command.Parameters.AddWithValue("chave_negocio", candidato.ChaveNegocio);
        command.Parameters.AddWithValue("fingerprint_sha256", candidato.FingerprintSha256);
        command.Parameters.AddWithValue("correlation_id", candidato.CorrelationId);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows == 1 ? ResultadoRegistroIntegracao.Criada : ResultadoRegistroIntegracao.JaExistente;
    }

    public async Task<IReadOnlyList<IntegracaoMotor>> SelecionarCandidatosPreClaimAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            select i.id, i.origem, i.destino, i.operacao, i.chave_negocio, i.fingerprint_sha256,
                   i.correlation_id, i.estado, i.tipo_disparo_pendente, i.proxima_tentativa_em,
                   i.fencing_token, i.worker_proprietario, i.lease_expira_em, i.fase_envio, i.tentativa_atual_id
            from integracao i
            where i.estado in ('PENDENTE', 'AGUARDANDO_REPROCESSAMENTO')
              and i.proxima_tentativa_em is not null
              and i.proxima_tentativa_em <= clock_timestamp()
              and (i.lease_expira_em is null or i.lease_expira_em <= clock_timestamp())
              and not exists (
                  select 1 from pausa_processamento p
                  where p.ativa and (
                      p.escopo = 'GERAL'
                      or (p.escopo = 'SISTEMA_EXTERNO' and p.sistema_externo in (i.origem, i.destino))
                      or (p.escopo = 'OPERACAO' and p.operacao = i.operacao)
                  )
              )
            order by i.criado_em;
            """;
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var itens = new List<IntegracaoMotor>();
        while (await reader.ReadAsync(cancellationToken))
        {
            itens.Add(LerIntegracao(reader));
        }

        return itens;
    }

    public async Task<ClaimIntegracaoMotor?> ClaimarEspecificamenteAsync(Guid integracaoId, EstadoIntegracao estadoEsperado, TipoDisparoPendente? tipoDisparoEsperado, string fingerprintEsperado, IdentidadeWorkerIntegracao worker, TimeSpan leaseDuration, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        const string selectSql = """
            -- contrato externo: :integracao_id, :novo_fencing_token, :nova_tentativa_id
            select i.id, i.origem, i.destino, i.operacao, i.chave_negocio, i.fingerprint_sha256,
                   i.correlation_id, i.fencing_token + 1 as novo_fencing_token,
                   coalesce(i.tipo_disparo_pendente, 'AUTOMATICO') as tipo_disparo_claim
            from integracao i
            where i.id = @integracao_id
              and i.fingerprint_sha256 = @fingerprint_esperado
              and i.estado = @estado_esperado
              and (i.tipo_disparo_pendente is not distinct from @tipo_disparo_esperado)
              and i.proxima_tentativa_em is not null
              and i.proxima_tentativa_em <= clock_timestamp()
              and (i.lease_expira_em is null or i.lease_expira_em <= clock_timestamp())
              and not exists (
                  select 1 from pausa_processamento p
                  where p.ativa and (
                      p.escopo = 'GERAL'
                      or (p.escopo = 'SISTEMA_EXTERNO' and p.sistema_externo in (i.origem, i.destino))
                      or (p.escopo = 'OPERACAO' and p.operacao = i.operacao)
                  )
              )
            for update skip locked;
            """;
        const string insertSql = """
            insert into tentativa_integracao (id, integracao_id, fencing_token, tipo_disparo, fase_envio, desfecho, criada_em)
            values (@nova_tentativa_id, @integracao_id, @novo_fencing_token, @tipo_disparo_claim,
                    'NAO_INICIADO', 'EM_EXECUCAO', clock_timestamp());
            """;
        const string updateSql = """
            update integracao i
            set estado = 'EM_PROCESSAMENTO',
                fencing_token = @novo_fencing_token,
                worker_proprietario = @worker,
                lease_expira_em = clock_timestamp() + @lease_duration,
                tentativa_atual_id = @nova_tentativa_id,
                fase_envio = 'NAO_INICIADO',
                proxima_tentativa_em = null,
                tipo_disparo_pendente = null,
                atualizado_em = clock_timestamp()
            where i.id = @integracao_id
              and i.fingerprint_sha256 = @fingerprint_esperado
              and i.estado = @estado_esperado
              and (i.tipo_disparo_pendente is not distinct from @tipo_disparo_esperado)
              and i.proxima_tentativa_em is not null
              and i.proxima_tentativa_em <= clock_timestamp()
              and (i.lease_expira_em is null or i.lease_expira_em <= clock_timestamp())
              and not exists (
                  select 1 from pausa_processamento p
                  where p.ativa and (
                      p.escopo = 'GERAL'
                      or (p.escopo = 'SISTEMA_EXTERNO' and p.sistema_externo in (i.origem, i.destino))
                      or (p.escopo = 'OPERACAO' and p.operacao = i.operacao)
                  )
              )
            returning i.id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var select = new NpgsqlCommand(selectSql, connection, transaction);
            select.Parameters.AddWithValue("integracao_id", integracaoId);
            select.Parameters.AddWithValue("fingerprint_esperado", fingerprintEsperado);
            select.Parameters.AddWithValue("estado_esperado", EstadoParaBanco(estadoEsperado));
            select.Parameters.AddWithValue("tipo_disparo_esperado", (object?)TipoDisparoParaBanco(tipoDisparoEsperado) ?? DBNull.Value);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var claim = new ClaimSelecionado(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                TipoDisparoDeBanco(reader.GetString(8)));
            await reader.DisposeAsync();

            var tentativaId = Guid.NewGuid();
            await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
            {
                insert.Parameters.AddWithValue("nova_tentativa_id", tentativaId);
                insert.Parameters.AddWithValue("integracao_id", claim.Id);
                insert.Parameters.AddWithValue("novo_fencing_token", claim.NovoFencingToken);
                insert.Parameters.AddWithValue("tipo_disparo_claim", TipoDisparoParaBanco(claim.TipoDisparo)!);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
            {
                update.Parameters.AddWithValue("novo_fencing_token", claim.NovoFencingToken);
                update.Parameters.AddWithValue("worker", worker.Valor);
                update.Parameters.AddWithValue("lease_duration", leaseDuration);
                update.Parameters.AddWithValue("nova_tentativa_id", tentativaId);
                update.Parameters.AddWithValue("integracao_id", claim.Id);
                update.Parameters.AddWithValue("fingerprint_esperado", fingerprintEsperado);
                update.Parameters.AddWithValue("estado_esperado", EstadoParaBanco(estadoEsperado));
                update.Parameters.AddWithValue("tipo_disparo_esperado", (object?)TipoDisparoParaBanco(tipoDisparoEsperado) ?? DBNull.Value);
                var updated = await update.ExecuteScalarAsync(cancellationToken);
                if (updated is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
            }

            await RegistrarAuditoriaAsync(connection, transaction, claim.Id, "claim", worker.Valor, claim.NovoFencingToken, tentativaId, claim.CorrelationId, null, cancellationToken);
            await RegistrarAuditoriaAsync(connection, transaction, claim.Id, "tentativa_criada", worker.Valor, claim.NovoFencingToken, tentativaId, claim.CorrelationId, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ClaimIntegracaoMotor(claim.Id, tentativaId, claim.CorrelationId, claim.Origem, claim.Destino, claim.Operacao, claim.ChaveNegocio, claim.FingerprintSha256, claim.NovoFencingToken, worker.Valor, agoraUtc.Add(leaseDuration));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> RegistrarValidacaoReprovadaAsync(Guid integracaoId, string correlationId, string diagnosticoSanitizado, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await RegistrarAuditoriaAsync(connection, transaction, integracaoId, "VALIDACAO_REPROVADA", null, null, null, correlationId, diagnosticoSanitizado, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IntegracaoMotor?> ObterPorIdAsync(Guid integracaoId, CancellationToken cancellationToken)
    {
        const string sql = """
            select i.id, i.origem, i.destino, i.operacao, i.chave_negocio, i.fingerprint_sha256,
                   i.correlation_id, i.estado, i.tipo_disparo_pendente, i.proxima_tentativa_em,
                   i.fencing_token, i.worker_proprietario, i.lease_expira_em, i.fase_envio, i.tentativa_atual_id
            from integracao i where i.id = @integracao_id;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("integracao_id", integracaoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? LerIntegracao(reader) : null;
    }

    public async Task<bool> MarcarEnvioIniciadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            update integracao i
            set fase_envio = 'ENVIO_INICIADO',
                atualizado_em = clock_timestamp()
            where i.id = @integracao_id
              and i.estado = 'EM_PROCESSAMENTO'
              and i.fase_envio = 'NAO_INICIADO'
              and i.tentativa_atual_id = @tentativa_id
              and i.fencing_token = @fencing_token
              and i.worker_proprietario = @worker
              and i.lease_expira_em > clock_timestamp();
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddFencing(command, integracaoId, tentativaId, fencingToken, worker);
        var accepted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        var correlationId = accepted ? await ObterCorrelationIdAsync(connection, transaction, integracaoId, cancellationToken) : null;
        await RegistrarAuditoriaAsync(connection, transaction, integracaoId, accepted ? "envio_iniciado" : "marker_rejeitado", worker, fencingToken, tentativaId, correlationId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    public async Task<bool> RenovarLeaseAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, TimeSpan leaseDuration, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            update integracao i
            set lease_expira_em = clock_timestamp() + @lease_duration,
                atualizado_em = clock_timestamp()
            where i.id = @integracao_id
              and i.estado = 'EM_PROCESSAMENTO'
              and i.tentativa_atual_id = @tentativa_id
              and i.fencing_token = @fencing_token
              and i.worker_proprietario = @worker
              and i.lease_expira_em > clock_timestamp();
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddFencing(command, integracaoId, tentativaId, fencingToken, worker);
        command.Parameters.AddWithValue("lease_duration", leaseDuration);
        var accepted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        var correlationId = accepted ? await ObterCorrelationIdAsync(connection, transaction, integracaoId, cancellationToken) : null;
        await RegistrarAuditoriaAsync(connection, transaction, integracaoId, accepted ? "lease_renew" : "lease_renew_rejeitado", worker, fencingToken, tentativaId, correlationId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    public async Task<bool> PersistirResultadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, ResultadoProcessamentoExterno resultado, DateTimeOffset agoraUtc, TimeSpan retryBackoff, CancellationToken cancellationToken)
    {
        if (resultado.Falha == ClassificacaoFalhaIntegracao.ResultadoIndeterminado)
        {
            await RegistrarAuditoriaIsoladaAsync(integracaoId, "indeterminado_no_caminho_generico_rejeitado", worker, fencingToken, tentativaId, null, cancellationToken);
            return false;
        }

        var novoEstado = MaquinaEstadoIntegracao.AplicarResultado(resultado.Falha);
        var agendaRetry = novoEstado == EstadoIntegracao.AguardandoReprocessamento;
        var desfechoTentativa = resultado.Falha is null ? "SUCESSO" : "FALHA";
        const string integracaoSql = """
            update integracao i
            set estado = @novo_estado,
                tipo_disparo_pendente = @tipo_disparo_pendente,
                proxima_tentativa_em = case when @agenda_retry then clock_timestamp() + @retry_backoff else null end,
                worker_proprietario = null,
                lease_expira_em = null,
                fase_envio = 'RESULTADO_RECEBIDO',
                atualizado_em = clock_timestamp()
            where i.id = @integracao_id
              and i.estado = 'EM_PROCESSAMENTO'
              and i.fase_envio = 'ENVIO_INICIADO'
              and i.tentativa_atual_id = @tentativa_id
              and i.fencing_token = @fencing_token
              and i.worker_proprietario = @worker
              and i.lease_expira_em > clock_timestamp();
            """;
        const string tentativaSql = """
            update tentativa_integracao t
            set fase_envio = 'RESULTADO_RECEBIDO',
                desfecho = @desfecho_tentativa,
                resultado_recebido_em = clock_timestamp()
            where t.id = @tentativa_id
              and t.integracao_id = @integracao_id
              and t.fencing_token = @fencing_token
              and t.desfecho = 'EM_EXECUCAO';
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using (var integracao = new NpgsqlCommand(integracaoSql, connection, transaction))
            {
                AddFencing(integracao, integracaoId, tentativaId, fencingToken, worker);
                integracao.Parameters.AddWithValue("novo_estado", EstadoParaBanco(novoEstado));
                integracao.Parameters.AddWithValue("tipo_disparo_pendente", agendaRetry ? "AUTOMATICO" : DBNull.Value);
                integracao.Parameters.AddWithValue("agenda_retry", agendaRetry);
                integracao.Parameters.AddWithValue("retry_backoff", retryBackoff);
                if (await integracao.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    await RegistrarAuditoriaIsoladaAsync(integracaoId, "stale_writer_rejeitado", worker, fencingToken, tentativaId, null, cancellationToken);
                    return false;
                }
            }

            await using (var tentativa = new NpgsqlCommand(tentativaSql, connection, transaction))
            {
                tentativa.Parameters.AddWithValue("tentativa_id", tentativaId);
                tentativa.Parameters.AddWithValue("integracao_id", integracaoId);
                tentativa.Parameters.AddWithValue("fencing_token", fencingToken);
                tentativa.Parameters.AddWithValue("desfecho_tentativa", desfechoTentativa);
                if (await tentativa.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            var correlationId = await ObterCorrelationIdAsync(connection, transaction, integracaoId, cancellationToken);
            await RegistrarAuditoriaAsync(connection, transaction, integracaoId, "resultado", worker, fencingToken, tentativaId, correlationId, null, cancellationToken);
            await RegistrarAuditoriaAsync(connection, transaction, integracaoId, agendaRetry ? "retry_agendado" : "terminal", worker, fencingToken, tentativaId, correlationId, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> RegistrarResultadoIndeterminadoAsync(Guid integracaoId, Guid tentativaId, long fencingToken, string worker, DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        const string integracaoSql = """
            update integracao i
            set estado = 'RESULTADO_INDETERMINADO',
                tipo_disparo_pendente = null,
                proxima_tentativa_em = null,
                worker_proprietario = null,
                lease_expira_em = null,
                atualizado_em = clock_timestamp()
            where i.id = @integracao_id
              and i.estado = 'EM_PROCESSAMENTO'
              and i.fase_envio = 'ENVIO_INICIADO'
              and i.tentativa_atual_id = @tentativa_id
              and i.fencing_token = @fencing_token
              and i.worker_proprietario = @worker
              and i.lease_expira_em > clock_timestamp();
            """;
        const string tentativaSql = """
            update tentativa_integracao t
            set desfecho = 'INDETERMINADA'
            where t.id = @tentativa_id
              and t.integracao_id = @integracao_id
              and t.fencing_token = @fencing_token
              and t.desfecho = 'EM_EXECUCAO';
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using (var integracao = new NpgsqlCommand(integracaoSql, connection, transaction))
            {
                AddFencing(integracao, integracaoId, tentativaId, fencingToken, worker);
                if (await integracao.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    await RegistrarAuditoriaIsoladaAsync(integracaoId, "indeterminado_rejeitado", worker, fencingToken, tentativaId, null, cancellationToken);
                    return false;
                }
            }

            await using (var tentativa = new NpgsqlCommand(tentativaSql, connection, transaction))
            {
                tentativa.Parameters.AddWithValue("tentativa_id", tentativaId);
                tentativa.Parameters.AddWithValue("integracao_id", integracaoId);
                tentativa.Parameters.AddWithValue("fencing_token", fencingToken);
                if (await tentativa.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            var correlationId = await ObterCorrelationIdAsync(connection, transaction, integracaoId, cancellationToken);
            await RegistrarAuditoriaAsync(connection, transaction, integracaoId, "resultado_indeterminado", worker, fencingToken, tentativaId, correlationId, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> ReprocessarAsync(Guid integracaoId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken)
    {
        const string sql = """
            update integracao
            set estado = 'AGUARDANDO_REPROCESSAMENTO',
                tipo_disparo_pendente = 'REPROCESSAMENTO',
                proxima_tentativa_em = clock_timestamp(),
                correlation_id = @correlation_id,
                atualizado_em = clock_timestamp()
            where id = @integracao_id
              and estado = 'AGUARDANDO_ACAO_MANUAL';
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("integracao_id", integracaoId);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        var accepted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (accepted)
            await RegistrarAuditoriaAsync(connection, transaction, integracaoId, "reprocessar", null, null, null, correlationId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    public async Task<bool> IntegrarAgoraAsync(Guid integracaoId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken)
    {
        const string sql = """
            update integracao
            set tipo_disparo_pendente = 'INTEGRAR_AGORA',
                proxima_tentativa_em = clock_timestamp(),
                correlation_id = @correlation_id,
                atualizado_em = clock_timestamp()
            where id = @integracao_id
              and estado = 'PENDENTE';
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("integracao_id", integracaoId);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        var accepted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (accepted)
            await RegistrarAuditoriaAsync(connection, transaction, integracaoId, "integrar_agora", null, null, null, correlationId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    public async Task<bool> CriarPausaAsync(PausaProcessamentoMotor pausa, string correlationId, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into pausa_processamento (id, escopo, sistema_externo, operacao, ativa, criada_em, retomada_em)
            values (@id, @escopo, @sistema_externo, @operacao, @ativa, clock_timestamp(), null)
            on conflict do nothing;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", pausa.Id);
        command.Parameters.AddWithValue("escopo", EscopoParaBanco(pausa.Escopo));
        command.Parameters.AddWithValue("sistema_externo", (object?)pausa.SistemaExterno ?? DBNull.Value);
        command.Parameters.AddWithValue("operacao", (object?)pausa.Operacao ?? DBNull.Value);
        command.Parameters.AddWithValue("ativa", pausa.Ativa);
        var accepted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (accepted)
            await RegistrarAuditoriaAsync(connection, transaction, null, "pausa", null, null, null, correlationId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    public async Task<bool> RetomarPausaAsync(Guid pausaId, DateTimeOffset agoraUtc, string correlationId, CancellationToken cancellationToken)
    {
        const string sql = """
            update pausa_processamento
            set ativa = false,
                retomada_em = clock_timestamp()
            where id = @pausa_id
              and ativa;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("pausa_id", pausaId);
        var accepted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (accepted)
            await RegistrarAuditoriaAsync(connection, transaction, null, "retomada", null, null, null, correlationId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    public async Task<int> RecuperarLeasesExpiradasAsync(DateTimeOffset agoraUtc, CancellationToken cancellationToken)
    {
        const string selectSql = """
            select i.id, i.tentativa_atual_id, i.fencing_token, i.fase_envio, i.worker_proprietario, i.correlation_id
            from integracao i
            where i.estado = 'EM_PROCESSAMENTO'
              and i.lease_expira_em <= clock_timestamp()
              and (
                  i.fase_envio = 'NAO_INICIADO'
                  or i.fase_envio = 'ENVIO_INICIADO'
              )
              and i.tentativa_atual_id is not null
            for update skip locked;
            """;
        const string tentativaSql = """
            update tentativa_integracao t
            set desfecho = case when @fase_envio = 'NAO_INICIADO' then 'INTERROMPIDA' else 'INDETERMINADA' end
            where t.id = @tentativa_id
              and t.integracao_id = @integracao_id
              and t.fencing_token = @fencing_token
              and t.desfecho = 'EM_EXECUCAO';
            """;
        const string integracaoSql = """
            update integracao i
            set estado = case when i.fase_envio = 'NAO_INICIADO' then 'AGUARDANDO_REPROCESSAMENTO' else 'RESULTADO_INDETERMINADO' end,
                tipo_disparo_pendente = case when i.fase_envio = 'NAO_INICIADO' then 'AUTOMATICO' else null end,
                proxima_tentativa_em = case when i.fase_envio = 'NAO_INICIADO' then clock_timestamp() else null end,
                worker_proprietario = null,
                lease_expira_em = null,
                tentativa_atual_id = case when i.fase_envio = 'NAO_INICIADO' then null else i.tentativa_atual_id end,
                atualizado_em = clock_timestamp()
            where i.estado = 'EM_PROCESSAMENTO'
              and i.id = @integracao_id
              and i.tentativa_atual_id = @tentativa_id
              and i.fencing_token = @fencing_token
              and i.lease_expira_em <= clock_timestamp()
              and i.fase_envio = @fase_envio;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var expiradas = new List<(Guid IntegracaoId, Guid TentativaId, long FencingToken, string FaseEnvio, string? Worker, string CorrelationId)>();
            await using (var select = new NpgsqlCommand(selectSql, connection, transaction))
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    expiradas.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
                }
            }

            foreach (var expirada in expiradas)
            {
                await using (var tentativa = new NpgsqlCommand(tentativaSql, connection, transaction))
                {
                    tentativa.Parameters.AddWithValue("tentativa_id", expirada.TentativaId);
                    tentativa.Parameters.AddWithValue("integracao_id", expirada.IntegracaoId);
                    tentativa.Parameters.AddWithValue("fencing_token", expirada.FencingToken);
                    tentativa.Parameters.AddWithValue("fase_envio", expirada.FaseEnvio);
                    if (await tentativa.ExecuteNonQueryAsync(cancellationToken) != 1)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return 0;
                    }
                }

                await using (var integracao = new NpgsqlCommand(integracaoSql, connection, transaction))
                {
                    integracao.Parameters.AddWithValue("integracao_id", expirada.IntegracaoId);
                    integracao.Parameters.AddWithValue("tentativa_id", expirada.TentativaId);
                    integracao.Parameters.AddWithValue("fencing_token", expirada.FencingToken);
                    integracao.Parameters.AddWithValue("fase_envio", expirada.FaseEnvio);
                    if (await integracao.ExecuteNonQueryAsync(cancellationToken) != 1)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return 0;
                    }
                }
                await RegistrarAuditoriaAsync(connection, transaction, expirada.IntegracaoId, "recovery", expirada.Worker, expirada.FencingToken, expirada.TentativaId, expirada.CorrelationId, null, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return expiradas.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<int> ExecuteSimpleAsync(string sql, Guid integracaoId, string correlationId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("integracao_id", integracaoId);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> ExecuteFencedAsync(string sql, Guid integracaoId, Guid tentativaId, long fencingToken, string worker, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        AddFencing(command, integracaoId, tentativaId, fencingToken, worker);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> ExecuteFencedWithAuditAsync(string sql, Guid integracaoId, Guid tentativaId, long fencingToken, string worker, string acceptedEvent, string rejectedEvent, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddFencing(command, integracaoId, tentativaId, fencingToken, worker);
        var accepted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        var correlationId = accepted ? await ObterCorrelationIdAsync(connection, transaction, integracaoId, cancellationToken) : null;
        await RegistrarAuditoriaAsync(connection, transaction, integracaoId, accepted ? acceptedEvent : rejectedEvent, worker, fencingToken, tentativaId, correlationId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    private async Task RegistrarAuditoriaIsoladaAsync(Guid? integracaoId, string evento, string? worker, long? fencingToken, Guid? tentativaId, string? correlationId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await RegistrarAuditoriaAsync(connection, transaction, integracaoId, evento, worker, fencingToken, tentativaId, correlationId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<string?> ObterCorrelationIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid integracaoId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select correlation_id from integracao where id = @integracao_id;", connection, transaction);
        command.Parameters.AddWithValue("integracao_id", integracaoId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task RegistrarAuditoriaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? integracaoId, string evento, string? worker, long? fencingToken, Guid? tentativaId, string? correlationId, string? diagnostico, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into auditoria_integracao
                (id, integracao_id, evento, criada_em, worker, fencing_token, tentativa_id, correlation_id, diagnostico)
            values
                (@id, @integracao_id, @evento, clock_timestamp(), @worker, @fencing_token, @tentativa_id, @correlation_id, @diagnostico);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("integracao_id", (object?)integracaoId ?? DBNull.Value);
        command.Parameters.AddWithValue("evento", evento);
        command.Parameters.AddWithValue("worker", (object?)worker ?? DBNull.Value);
        command.Parameters.AddWithValue("fencing_token", (object?)fencingToken ?? DBNull.Value);
        command.Parameters.AddWithValue("tentativa_id", (object?)tentativaId ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation_id", (object?)correlationId ?? DBNull.Value);
        command.Parameters.AddWithValue("diagnostico", (object?)diagnostico ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddFencing(NpgsqlCommand command, Guid integracaoId, Guid tentativaId, long fencingToken, string worker)
    {
        command.Parameters.AddWithValue("integracao_id", integracaoId);
        command.Parameters.AddWithValue("tentativa_id", tentativaId);
        command.Parameters.AddWithValue("fencing_token", fencingToken);
        command.Parameters.AddWithValue("worker", worker);
    }

    private static IntegracaoMotor LerIntegracao(NpgsqlDataReader reader)
    {
        return new IntegracaoMotor(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            EstadoDeBanco(reader.GetString(7)),
            reader.IsDBNull(8) ? null : TipoDisparoDeBanco(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            FaseDeBanco(reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetGuid(14));
    }

    private static string EstadoParaBanco(EstadoIntegracao estado)
    {
        return estado switch
        {
            EstadoIntegracao.Pendente => "PENDENTE",
            EstadoIntegracao.EmProcessamento => "EM_PROCESSAMENTO",
            EstadoIntegracao.AguardandoReprocessamento => "AGUARDANDO_REPROCESSAMENTO",
            EstadoIntegracao.AguardandoAcaoManual => "AGUARDANDO_ACAO_MANUAL",
            EstadoIntegracao.ResultadoIndeterminado => "RESULTADO_INDETERMINADO",
            EstadoIntegracao.Concluida => "CONCLUIDA",
            EstadoIntegracao.FalhaDefinitiva => "FALHA_DEFINITIVA",
            EstadoIntegracao.Cancelada => "CANCELADA",
            _ => throw new ArgumentOutOfRangeException(nameof(estado), estado, null)
        };
    }

    private static EstadoIntegracao EstadoDeBanco(string estado)
    {
        return estado switch
        {
            "PENDENTE" => EstadoIntegracao.Pendente,
            "EM_PROCESSAMENTO" => EstadoIntegracao.EmProcessamento,
            "AGUARDANDO_REPROCESSAMENTO" => EstadoIntegracao.AguardandoReprocessamento,
            "AGUARDANDO_ACAO_MANUAL" => EstadoIntegracao.AguardandoAcaoManual,
            "RESULTADO_INDETERMINADO" => EstadoIntegracao.ResultadoIndeterminado,
            "CONCLUIDA" => EstadoIntegracao.Concluida,
            "FALHA_DEFINITIVA" => EstadoIntegracao.FalhaDefinitiva,
            "CANCELADA" => EstadoIntegracao.Cancelada,
            _ => throw new InvalidOperationException("Estado de integracao desconhecido.")
        };
    }

    private static string? TipoDisparoParaBanco(TipoDisparoPendente? tipo)
    {
        return tipo switch
        {
            TipoDisparoPendente.Automatico => "AUTOMATICO",
            TipoDisparoPendente.IntegrarAgora => "INTEGRAR_AGORA",
            TipoDisparoPendente.Reprocessamento => "REPROCESSAMENTO",
            null => null,
            _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, null)
        };
    }

    private static TipoDisparoPendente TipoDisparoDeBanco(string tipo)
    {
        return tipo switch
        {
            "AUTOMATICO" => TipoDisparoPendente.Automatico,
            "INTEGRAR_AGORA" => TipoDisparoPendente.IntegrarAgora,
            "REPROCESSAMENTO" => TipoDisparoPendente.Reprocessamento,
            _ => throw new InvalidOperationException("Tipo de disparo desconhecido.")
        };
    }

    private static FaseEnvioIntegracao FaseDeBanco(string fase)
    {
        return fase switch
        {
            "NAO_INICIADO" => FaseEnvioIntegracao.NaoIniciado,
            "ENVIO_INICIADO" => FaseEnvioIntegracao.EnvioIniciado,
            "RESULTADO_RECEBIDO" => FaseEnvioIntegracao.ResultadoRecebido,
            _ => throw new InvalidOperationException("Fase de envio desconhecida.")
        };
    }

    private static string EscopoParaBanco(EscopoPausaProcessamento escopo)
    {
        return escopo switch
        {
            EscopoPausaProcessamento.Geral => "GERAL",
            EscopoPausaProcessamento.SistemaExterno => "SISTEMA_EXTERNO",
            EscopoPausaProcessamento.Operacao => "OPERACAO",
            _ => throw new ArgumentOutOfRangeException(nameof(escopo), escopo, null)
        };
    }

    private sealed record ClaimSelecionado(
        Guid Id,
        string Origem,
        string Destino,
        string Operacao,
        string ChaveNegocio,
        string FingerprintSha256,
        string CorrelationId,
        long NovoFencingToken,
        TipoDisparoPendente TipoDisparo);
}
