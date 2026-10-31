using System.Data;
using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.CasosDeUso.Mvp;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.Persistencia.MotorIntegracao;
using FugaTES.Persistencia.Mvp;
using Npgsql;

namespace FugaTES.MvpPostgreSqlE2E;

internal sealed record FixtureIdentity(Guid RunId)
{
    public string Key => "MVP-E2E-007A1-" + RunId.ToString("N");
    public string Worker => "E2E-" + RunId.ToString("N");
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("MVP_SDOUT\n" + Key)));
}

internal static class Runner
{
    public static readonly string[] Proofs = ["INSERT_REAL", "READBACK", "UPSERT", "CREATED_AT_PRESERVED",
        "STALE_FENCING_REJECTED", "WRONG_WORKER_REJECTED", "EXPIRED_LEASE_REJECTED",
        "PENDING_DECISION_PERSISTED", "NORMALIZED_ERROR", "INDETERMINATE", "PROTOCOL_TICKET_SEPARATE_STORAGE"];

    public static string ValidateRuntime(Func<string, string?> env)
    {
        if (env("FUGATES_MVP_E2E_ENABLED") != "true" || env("FUGATES_MVP_E2E_ISOLATED_SESSION") != "true")
            throw new InvalidOperationException("EXPLICIT_ISOLATED_SESSION_REQUIRED");
        var value = env("ConnectionStrings__FugaTES");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("RUNTIME_CONFIGURATION_REQUIRED");
        var config = new NpgsqlConnectionStringBuilder(value);
        if (config.Host != "127.0.0.1" || config.Port != 15432 || config.Database != "fugates_dev" ||
            string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password) ||
            config.SearchPath != "fugates") throw new InvalidOperationException("RUNTIME_TOPOLOGY_INVALID");
        config.IncludeErrorDetail = false;
        return config.ConnectionString;
    }

    public static async Task<int> RunAsync(string connection, Guid? cleanupRun, CancellationToken ct)
    {
        var identity = new FixtureIdentity(cleanupRun ?? Guid.NewGuid());
        Console.WriteLine($"FIXTURE_RUN_ID={identity.RunId:D}");
        await using var source = NpgsqlDataSourceFactory.Criar(connection);
        var passed = new HashSet<string>(StringComparer.Ordinal);
        var started = false;
        var cleanupPassed = false;
        var success = false;
        try
        {
            await PreflightAsync(source, ct);
            Console.WriteLine("DATABASE_CONNECTED=PASS");
            if (cleanupRun is not null)
            {
                started = true;
                success = true;
            }
            else
            {
                if (await LocateAsync(source, identity, ct) is not null) throw new InvalidOperationException("FIXTURE_ALREADY_EXISTS");
                started = true;
                var motor = new RepositorioMotorIntegracaoPostgreSql(source);
                IResultadoFluxoMvpStore store = new RepositorioResultadoFluxoMvpPostgreSql(source);
                Console.WriteLine("PRODUCTION_STORE_USED=SIM");
                var pending = new PendingProbe(motor, store);
                Require(pending.Fingerprint(identity.Key) == identity.Fingerprint);
                var registered = await motor.RegistrarCandidatoAsync(new("SAP", "GUARDIAN", "MVP_SDOUT",
                    identity.Key, identity.Fingerprint, identity.Key), DateTimeOffset.UtcNow, ct);
                Require(registered == ResultadoRegistroIntegracao.Criada);
                var integration = await LocateAsync(source, identity, ct) ?? throw new InvalidOperationException("FIXTURE_NOT_FOUND");
                Console.WriteLine($"FIXTURE_INTEGRACAO_ID={integration:D}");
                var claim = await motor.ClaimarEspecificamenteAsync(integration, EstadoIntegracao.Pendente,
                    TipoDisparoPendente.Automatico, identity.Fingerprint, new(identity.Worker), TimeSpan.FromMinutes(2),
                    DateTimeOffset.UtcNow, ct) ?? throw new InvalidOperationException("CLAIM_REJECTED");
                var result = new ResultadoFluxoMvp(StatusFluxoMvp.Concluido, identity.Key, "MVP_SDOUT",
                    ResultadoFuncional: "E2E_SYNTHETIC", SalesOrder: "E2E_SO", DeliveryDocument: "E2E_DEL",
                    PurchaseOrder: "E2E_PO", MaterialDocument: "E2E_MD", MaterialDocumentYear: "2026",
                    ProtocoloGuardian: "E2E_PROTOCOL", NumeroTicketGuardian: "E2E_TICKET");
                Require(await store.PersistirAsync(claim, result, ct)); Pass("INSERT_REAL", passed);
                Require(await store.ObterAsync(claim, ct) == result.Normalizar()); Pass("READBACK", passed);
                var first = await SnapshotAsync(source, integration, ct);
                var update = result with { ResultadoFuncional = "E2E_UPSERT" };
                Require(await store.PersistirAsync(claim, update, ct));
                Require(await store.ObterAsync(claim, ct) == update.Normalizar());
                var second = await SnapshotAsync(source, integration, ct);
                Require(second.Count == 1 && second.Updated > first.Updated); Pass("UPSERT", passed);
                Require(second.Created == first.Created); Pass("CREATED_AT_PRESERVED", passed);
                Require(!await store.PersistirAsync(claim with { FencingToken = claim.FencingToken - 1 }, result, ct));
                Require(await SnapshotAsync(source, integration, ct) == second); Pass("STALE_FENCING_REJECTED", passed);
                Require(!await store.PersistirAsync(claim with { Worker = "E2E_OTHER_WORKER" }, result, ct));
                Require(await SnapshotAsync(source, integration, ct) == second); Pass("WRONG_WORKER_REJECTED", passed);
                // The productive base persists the pending decision before returning; the probe has no HTTP dependencies.
                Require(await store.PersistirAsync(claim, result with { Status = StatusFluxoMvp.EmProcessamento }, ct));
                var decision = await pending.ExecutarAsync(claim, ct);
                Require(decision.Status == StatusFluxoMvp.AguardandoDecisaoFuncional &&
                    await store.ObterAsync(claim, ct) == decision.Normalizar() && pending.ExternalCalls == 0);
                Pass("PENDING_DECISION_PERSISTED", passed); Console.WriteLine("PENDING_DECISION_EXTERNAL_CALLS=0");
                var error = result with { Status = StatusFluxoMvp.FalhaReprocessavel, ErroCodigo = "E2E_SAFE_FAILURE", ErroCategoria = "E2E" };
                Require(await store.PersistirAsync(claim, error, ct));
                Require(await store.ObterAsync(claim, ct) == error.Normalizar()); Pass("NORMALIZED_ERROR", passed);
                var unknown = error with { Status = StatusFluxoMvp.ResultadoIndeterminado };
                Require(await store.PersistirAsync(claim, unknown, ct));
                var loaded = await store.ObterAsync(claim, ct);
                Require(loaded == unknown.Normalizar() && loaded!.ErroResultadoIndeterminado); Pass("INDETERMINATE", passed);
                Require(loaded!.ProtocoloGuardian == "E2E_PROTOCOL" && loaded.NumeroTicketGuardian == "E2E_TICKET" &&
                    loaded.ProtocoloGuardian != loaded.NumeroTicketGuardian); Pass("PROTOCOL_TICKET_SEPARATE_STORAGE", passed);
                // Expire only the owned fixture to exercise the database predicate, not the caller timestamp.
                await using (var expire = source.CreateCommand("update fugates.integracao set lease_expira_em = clock_timestamp() - interval '1 second' where id=@id and correlation_id=@key and fingerprint_sha256=@fingerprint and worker_proprietario=@worker"))
                {
                    Bind(expire, identity); expire.Parameters.AddWithValue("id", integration); expire.Parameters.AddWithValue("worker", identity.Worker);
                    Require(await expire.ExecuteNonQueryAsync(ct) == 1);
                }
                var beforeReject = await SnapshotAsync(source, integration, ct);
                Require(!await store.PersistirAsync(claim, result, ct));
                Require(await SnapshotAsync(source, integration, ct) == beforeReject); Pass("EXPIRED_LEASE_REJECTED", passed);
                success = true;
            }
        }
        catch { Console.WriteLine("RUNNER_STATUS=FAIL_CLOSED"); }
        finally
        {
            if (started)
            {
                using var cleanupToken = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await CleanupAsync(source, identity, cleanupToken.Token); cleanupPassed = true; }
                catch { Console.WriteLine("CLEANUP_RECOVERY_REQUIRED=SIM"); }
            }
            Console.WriteLine("FIXTURE_CLEANUP=" + (started ? cleanupPassed ? "PASS" : "FAIL" : "NOT_REQUIRED"));
        }
        if (cleanupRun is null)
            foreach (var proof in Proofs.Where(p => !passed.Contains(p))) Console.WriteLine(proof + "=FAIL");
        Console.WriteLine("E2E_STATUS=" + (success && cleanupPassed ? "PASS" : "FAIL"));
        return success && cleanupPassed ? 0 : 1;
    }

    private static async Task PreflightAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var command = source.CreateCommand("""
            select current_database()='fugates_dev' and current_schema()='fugates'
              and current_setting('server_version_num')::int between 180000 and 189999
              and to_regclass('fugates.projecao_resultado_fluxo_mvp') is not null
              and (select bool_and(has_table_privilege(current_user,t.tab,p.priv))
                   from (values ('fugates.integracao'), ('fugates.tentativa_integracao'),
                       ('fugates.auditoria_integracao'), ('fugates.projecao_resultado_fluxo_mvp')) t(tab)
                   cross join (values ('SELECT'), ('INSERT'), ('DELETE')) p(priv))
              and has_table_privilege(current_user,'fugates.integracao','UPDATE')
              and has_table_privilege(current_user,'fugates.projecao_resultado_fluxo_mvp','UPDATE');
            """);
        Require(await command.ExecuteScalarAsync(ct) is true);
    }

    private static void Bind(NpgsqlCommand command, FixtureIdentity identity)
    {
        command.Parameters.AddWithValue("key", identity.Key);
        command.Parameters.AddWithValue("fingerprint", identity.Fingerprint);
    }

    private static async Task<Guid?> LocateAsync(NpgsqlDataSource source, FixtureIdentity identity, CancellationToken ct)
    {
        await using var command = source.CreateCommand("select id from fugates.integracao where fingerprint_sha256=@fingerprint and correlation_id=@key and chave_negocio=@key and operacao='MVP_SDOUT'");
        Bind(command, identity);
        return await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }

    private sealed record Snapshot(long Count, DateTime Created, DateTime Updated, string Row);
    private static async Task<Snapshot> SnapshotAsync(NpgsqlDataSource source, Guid id, CancellationToken ct)
    {
        await using var command = source.CreateCommand("select count(*) over(), criado_em, atualizado_em, row_to_json(p)::text from fugates.projecao_resultado_fluxo_mvp p where integracao_id=@id");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Require(await reader.ReadAsync(ct));
        return new(reader.GetInt64(0), reader.GetDateTime(1), reader.GetDateTime(2), reader.GetString(3));
    }

    internal static readonly string CleanupSql = """
        update fugates.integracao set estado='CANCELADA', tentativa_atual_id=null,
            worker_proprietario=null, lease_expira_em=null, proxima_tentativa_em=null
            where id=@id and correlation_id=@key and fingerprint_sha256=@fingerprint;
        delete from fugates.projecao_resultado_fluxo_mvp where integracao_id=@id;
        delete from fugates.auditoria_integracao where integracao_id=@id;
        delete from fugates.tentativa_integracao where integracao_id=@id;
        delete from fugates.integracao where id=@id and correlation_id=@key and fingerprint_sha256=@fingerprint;
        """;

    private static async Task CleanupAsync(NpgsqlDataSource source, FixtureIdentity identity, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var locked = new NpgsqlCommand("select id, worker_proprietario from fugates.integracao where fingerprint_sha256=@fingerprint and correlation_id=@key and chave_negocio=@key and operacao='MVP_SDOUT' for update", connection, transaction);
        Bind(locked, identity);
        Guid? id = null;
        await using (var reader = await locked.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                id = reader.GetGuid(0);
                Require(reader.IsDBNull(1) || reader.GetString(1) == identity.Worker);
                Require(!await reader.ReadAsync(ct));
            }
        }
        if (id is not null)
        {
            await using var cleanup = new NpgsqlCommand(CleanupSql, connection, transaction);
            Bind(cleanup, identity); cleanup.Parameters.AddWithValue("id", id.Value);
            await cleanup.ExecuteNonQueryAsync(ct);
            await using var verify = new NpgsqlCommand("select (select count(*) from fugates.integracao where id=@id) + (select count(*) from fugates.projecao_resultado_fluxo_mvp where integracao_id=@id) + (select count(*) from fugates.tentativa_integracao where integracao_id=@id) + (select count(*) from fugates.auditoria_integracao where integracao_id=@id)", connection, transaction);
            verify.Parameters.AddWithValue("id", id.Value);
            Require(Convert.ToInt64(await verify.ExecuteScalarAsync(ct)) == 0);
        }
        await transaction.CommitAsync(ct);
    }

    private static void Pass(string name, HashSet<string> passed) { passed.Add(name); Console.WriteLine(name + "=PASS"); }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("E2E_ASSERTION_FAILED"); }

    private sealed class PendingDecision : IDecisoesFluxoMvp
    {
        public void Exigir(string decisao) => throw new DecisaoFuncionalPendenteSapException("E2E_PENDING_DECISION");
    }

    private sealed class PendingProbe(RepositorioMotorIntegracaoPostgreSql motor, IResultadoFluxoMvpStore store)
        : FluxoMvpCasoDeUso(FluxoMvp.SdOut, motor, store, new PendingDecision(), TimeProvider.System)
    {
        public int ExternalCalls { get; private set; }
        protected override Task<ResultadoFluxoMvp> ExecutarEtapaAsync(ClaimIntegracaoMotor claim, EfeitosFluxoMvp efeitos, CancellationToken ct)
        {
            ExternalCalls++;
            throw new InvalidOperationException("UNEXPECTED_EXTERNAL_STAGE");
        }
    }
}
