using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.HostIntegracao;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Persistencia.Mvp;

namespace FugaTES.Testes;

public sealed class MvpFlowResultProjectionTests
{
    [Theory]
    [InlineData("ResultadoFuncional", typeof(string))]
    [InlineData("SalesOrder", typeof(string))]
    [InlineData("DeliveryDocument", typeof(string))]
    [InlineData("PurchaseOrder", typeof(string))]
    [InlineData("MaterialDocument", typeof(string))]
    [InlineData("MaterialDocumentYear", typeof(string))]
    [InlineData("ProtocoloGuardian", typeof(string))]
    [InlineData("NumeroTicketGuardian", typeof(string))]
    [InlineData("ErroCodigo", typeof(string))]
    [InlineData("ErroCategoria", typeof(string))]
    [InlineData("ErroEtapa", typeof(string))]
    [InlineData("ErroReprocessavel", typeof(bool?))]
    [InlineData("ErroResultadoIndeterminado", typeof(bool))]
    public void Resultado_transporta_identidades_e_erro_tipados(string name, Type type)
        => Assert.Equal(type, typeof(ResultadoFluxoMvp).GetProperty(name)?.PropertyType);

    [Fact]
    public void Composition_com_datasource_registra_store_real_sem_abrir_conexao()
    {
        using var source = NpgsqlDataSource.Create("Host=offline.invalid;Database=offline;Username=offline");
        var services = new ServiceCollection();
        services.AddSingleton(source);
        services.AddMvpOrchestration();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.Equal("RepositorioResultadoFluxoMvpPostgreSql",
            scope.ServiceProvider.GetRequiredService<IResultadoFluxoMvpStore>().GetType().Name);
    }

    [Theory]
    [InlineData("MVP_SDOUT", "SD_OUT")]
    [InlineData("MVP_MMINPRECADASTRO", "MM_IN")]
    [InlineData("MVP_MMIN101", "MM_IN")]
    [InlineData("MVP_ACMINCONTRATO", "ACM_IN")]
    [InlineData("MVP_ACMINPESAGEM", "ACM_IN")]
    [InlineData("MVP_ACMOUT", "ACM_OUT")]
    public async Task Todos_fluxos_persistem_com_claim_na_mesma_transacao(string operation, string flow)
    {
        using var db = new OfflineSource(Claim(operation));
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        Assert.True(await store.PersistirAsync(db.Current, Result(db.Current), CancellationToken.None));
        Assert.Equal(flow, db.Row!["fluxo"]);
        Assert.Equal(2, db.Commands.Count);
        Assert.All(db.Commands, command => Assert.Same(db.Transaction, command.Transaction));
        Assert.Equal(IsolationLevel.ReadCommitted, db.Transaction!.IsolationLevel);
        Assert.Equal(1, db.Transaction.Commits);
        Assert.Contains("for update", db.Commands[0].Sql);
        var sql = db.Commands[1].Sql;
        Assert.Contains("on conflict (integracao_id) do update", sql);
        var update = sql[sql.IndexOf("do update", StringComparison.Ordinal)..];
        Assert.DoesNotContain("criado_em =", update);
        Assert.Contains("atualizado_em = clock_timestamp()", update);
        Assert.DoesNotContain("fencing_token", sql[..sql.IndexOf("select i.id", StringComparison.Ordinal)]);
    }

    [Fact]
    public async Task Replay_mantem_uma_linha_timestamp_e_rehidrata_identidades()
    {
        using var db = new OfflineSource(Claim());
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        var result = Result(db.Current);
        Assert.True(await store.PersistirAsync(db.Current, result, CancellationToken.None));
        var created = db.Created;
        var updated = db.Updated;
        Assert.True(await store.PersistirAsync(db.Current, result with { ResultadoFuncional = "REPLAY" }, CancellationToken.None));
        Assert.Equal(created, db.Created);
        Assert.True(db.Updated > updated);
        Assert.Equal(1, db.RowCount);
        var reloaded = await store.ObterAsync(db.Current, CancellationToken.None);
        Assert.Equal(result.Normalizar() with { ResultadoFuncional = "REPLAY" }, reloaded);
        Assert.DoesNotContain("correlation_id", db.Row!.Keys);
        Assert.DoesNotContain("fencing_token", db.Row.Keys);
        Assert.Equal("PROTOCOL-TEST", reloaded!.ProtocoloGuardian);
        Assert.Equal("TICKET-INDEPENDENT", reloaded.NumeroTicketGuardian);
        Assert.Equal("SO-TEST", reloaded.SalesOrder);
        Assert.Equal("DEL-TEST", reloaded.DeliveryDocument);
        Assert.Equal("PO-TEST", reloaded.PurchaseOrder);
        Assert.Equal("MD-TEST", reloaded.MaterialDocument);
        Assert.Equal("2026", reloaded.MaterialDocumentYear);
    }

    [Theory]
    [InlineData("fencing")]
    [InlineData("worker")]
    [InlineData("tentativa")]
    [InlineData("lease")]
    [InlineData("estado")]
    [InlineData("correlacao")]
    public async Task Claim_INC005_invalido_rejeita_antes_do_upsert(string invalid)
    {
        using var db = new OfflineSource(Claim());
        var claim = db.Current;
        if (invalid == "fencing") claim = claim with { FencingToken = claim.FencingToken - 1 };
        if (invalid == "worker") claim = claim with { Worker = "STALE" };
        if (invalid == "tentativa") claim = claim with { TentativaId = Guid.NewGuid() };
        if (invalid == "correlacao") claim = claim with { CorrelationId = "OTHER" };
        if (invalid == "lease") db.Expired = true;
        if (invalid == "estado") db.Processing = false;
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        Assert.False(await store.PersistirAsync(claim, Result(claim), CancellationToken.None));
        Assert.Null(db.Row);
        var command = Assert.Single(db.Commands);
        foreach (var condition in new[] { "tentativa_atual_id = @tentativa_id", "fencing_token = @fencing_token",
            "worker_proprietario = @worker", "estado = 'EM_PROCESSAMENTO'", "lease_expira_em > clock_timestamp()",
            "correlation_id = @correlation_id" }) Assert.Contains(condition, command.Sql);
        Assert.Equal(1, db.Transaction!.Rollbacks);
        Assert.Equal(0, db.Transaction.Commits);
    }

    [Fact]
    public async Task Lease_expirando_apos_lock_rejeita_upsert()
    {
        using var db = new OfflineSource(Claim()) { ExpireAfterLock = true };
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        Assert.False(await store.PersistirAsync(db.Current, Result(db.Current), CancellationToken.None));
        Assert.Null(db.Row);
        Assert.Contains("lease_expira_em > clock_timestamp()", db.Commands[1].Sql);
        Assert.Equal(1, db.Transaction!.Rollbacks);
    }

    [Fact]
    public async Task Correlacao_do_resultado_divergente_nao_abre_conexao()
    {
        using var db = new OfflineSource(Claim());
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        Assert.False(await store.PersistirAsync(db.Current, Result(db.Current) with { Correlacao = "OTHER" }, CancellationToken.None));
        Assert.Equal(0, db.Opens);
    }

    [Fact]
    public async Task Operacao_desconhecida_fail_closed_sem_conexao()
    {
        using var db = new OfflineSource(Claim("UNKNOWN"));
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PersistirAsync(db.Current, Result(db.Current), CancellationToken.None));
        Assert.Equal(0, db.Opens);
    }

    [Theory]
    [InlineData(StatusFluxoMvp.AguardandoDecisaoFuncional, "AGUARDANDO_DECISAO_FUNCIONAL")]
    [InlineData(StatusFluxoMvp.ResultadoIndeterminado, "RESULTADO_INDETERMINADO")]
    [InlineData(StatusFluxoMvp.FalhaReprocessavel, "FALHA_REPROCESSAVEL")]
    public async Task Status_decisao_e_erro_normalizado_roundtrip(StatusFluxoMvp status, string wire)
    {
        using var db = new OfflineSource(Claim());
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        var result = Result(db.Current) with { Status = status, DecisaoPendente = "API7",
            ErroCodigo = "CAPABILITY_PENDING", ErroCategoria = "DEPENDENCIA", ErroEtapa = "MVP_ACMOUT" };
        Assert.True(await store.PersistirAsync(db.Current, result, CancellationToken.None));
        Assert.Equal(wire, db.Row!["status_funcional"]);
        Assert.Equal(status == StatusFluxoMvp.ResultadoIndeterminado, db.Row["erro_resultado_indeterminado"]);
        Assert.Equal(result.Normalizar(), await store.ObterAsync(db.Current, CancellationToken.None));
    }

    [Theory]
    [InlineData("Password=TEST_SENTINEL")]
    [InlineData("Authorization: TEST_SENTINEL")]
    [InlineData("Bearer TEST_SENTINEL")]
    [InlineData("Basic TEST_SENTINEL")]
    [InlineData("cookie=TEST_SENTINEL")]
    [InlineData("assinatura=TEST_SENTINEL")]
    [InlineData("Host=offline.invalid")]
    [InlineData("StackTrace\nTEST_SENTINEL")]
    public async Task Campos_inseguros_nao_persistem_nem_abrem_conexao(string unsafeValue)
    {
        using var db = new OfflineSource(Claim());
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PersistirAsync(db.Current,
            Result(db.Current) with { ErroCodigo = unsafeValue }, CancellationToken.None));
        Assert.Equal(0, db.Opens);
        Assert.Null(db.Row);
    }

    [Fact]
    public async Task Tabela_ausente_nao_improvisa_fallback()
    {
        using var db = new OfflineSource(Claim()) { Available = false };
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        await Assert.ThrowsAsync<CapabilityMvpPendenteException>(() => store.ExigirDisponibilidadeAsync(CancellationToken.None));
        Assert.Null(db.Row);
    }

    [Fact]
    public async Task Excecao_no_upsert_rollback_dispose()
    {
        using var db = new OfflineSource(Claim()) { ThrowUpsert = true };
        var store = new RepositorioResultadoFluxoMvpPostgreSql(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PersistirAsync(db.Current, Result(db.Current), CancellationToken.None));
        Assert.Equal(1, db.Transaction!.Rollbacks);
        Assert.True(db.Transaction.Disposed);
        Assert.True(db.Connection!.WasDisposed);
    }

    private static ClaimIntegracaoMotor Claim(string operation = "MVP_SDOUT") => new(Guid.NewGuid(), Guid.NewGuid(),
        "CORR-TEST", "SAP", "GUARDIAN", operation, "SO-TEST", new string('A', 64), 7, "WORKER-TEST", DateTimeOffset.UtcNow.AddMinutes(1));
    private static ResultadoFluxoMvp Result(ClaimIntegracaoMotor claim) => new(StatusFluxoMvp.Concluido,
        claim.CorrelationId, claim.Operacao, ResultadoFuncional: "SUCCESS", SalesOrder: "SO-TEST",
        DeliveryDocument: "DEL-TEST", PurchaseOrder: "PO-TEST", MaterialDocument: "MD-TEST",
        MaterialDocumentYear: "2026", ProtocoloGuardian: "PROTOCOL-TEST", NumeroTicketGuardian: "TICKET-INDEPENDENT");

    [Fact]
    public async Task R57_atomic_stage_reservation_rejects_replay_and_preserves_transaction()
    {
        var claim=Claim("SAP_CONTRATO_TOLEDO");var db=new OfflineSource(claim);var store=new RepositorioResultadoFluxoMvpPostgreSql(db);
        var armed=new ResultadoFluxoMvp(StatusFluxoMvp.ResultadoIndeterminado,claim.CorrelationId,"R57_SOAP_ARMED",ResultadoFuncional:"R57::SYNTHETIC_HASH");
        Assert.True(await store.ReservarEtapaAsync(claim,null,armed,default));
        Assert.False(await store.ReservarEtapaAsync(claim,null,armed,default));
        Assert.Equal("R57_SOAP_ARMED",db.Row!["etapa_funcional"]);
        Assert.All(db.Commands.Where(c=>c.Sql.Contains("insert into")||c.Sql.Contains("for update")),c=>Assert.NotNull(c.Transaction));
    }
    [Fact]
    public async Task R57_atomic_stage_rejects_stale_fence_and_wrong_predecessor()
    {
        var claim=Claim("SAP_CONTRATO_TOLEDO");var db=new OfflineSource(claim);var store=new RepositorioResultadoFluxoMvpPostgreSql(db);
        var armed=new ResultadoFluxoMvp(StatusFluxoMvp.ResultadoIndeterminado,claim.CorrelationId,"R57_REST_ARMED");
        Assert.False(await store.ReservarEtapaAsync(claim with{FencingToken=6},null,armed,default));
        Assert.False(await store.ReservarEtapaAsync(claim,"R57_SOAP_CONFIRMED",armed,default));
        Assert.Null(db.Row);Assert.Equal(1,db.Transaction!.Rollbacks);
    }
    [Fact]
    public async Task R57_stage_can_advance_once_after_soap_confirmation()
    {
        var claim=Claim("SAP_CONTRATO_TOLEDO");var db=new OfflineSource(claim);var store=new RepositorioResultadoFluxoMvpPostgreSql(db);
        var confirmed=new ResultadoFluxoMvp(StatusFluxoMvp.EmProcessamento,claim.CorrelationId,"R57_SOAP_CONFIRMED",ResultadoFuncional:"R57:581:SYNTHETIC_HASH");
        Assert.True(await store.PersistirAsync(claim,confirmed,default));
        var armed=confirmed with{Status=StatusFluxoMvp.ResultadoIndeterminado,Etapa="R57_REST_ARMED"};
        Assert.True(await store.ReservarEtapaAsync(claim,"R57_SOAP_CONFIRMED",armed,default));
        Assert.False(await store.ReservarEtapaAsync(claim,"R57_SOAP_CONFIRMED",armed,default));
        Assert.Equal("R57_REST_ARMED",db.Row!["etapa_funcional"]);
    }
    private sealed class OfflineSource(ClaimIntegracaoMotor current) : DbDataSource
    {
        public ClaimIntegracaoMotor Current { get; } = current;
        public override string ConnectionString => "";
        public int Opens, RowCount;
        public bool Expired, ExpireAfterLock, ThrowUpsert;
        public bool Processing = true, Available = true;
        public OfflineConnection? Connection;
        public OfflineTransaction? Transaction;
        public Dictionary<string, object>? Row;
        public long Created, Updated;
        public List<(string Sql, DbTransaction? Transaction)> Commands { get; } = [];
        protected override DbConnection CreateDbConnection() => Connection = new(this);
        public bool Valid(DbCommand command) => !Expired && Processing &&
            Equals(command.Parameters["integracao_id"].Value, Current.IntegracaoId) &&
            Equals(command.Parameters["tentativa_id"].Value, Current.TentativaId) &&
            Equals(command.Parameters["fencing_token"].Value, Current.FencingToken) &&
            Equals(command.Parameters["worker"].Value, Current.Worker) &&
            Equals(command.Parameters["correlation_id"].Value, Current.CorrelationId);
    }

    private sealed class OfflineConnection(OfflineSource source) : DbConnection
    {
        public bool WasDisposed;
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "offline";
        public override string DataSource => "offline";
        public override string ServerVersion => "offline";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => source.Opens++;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => source.Transaction = new(this, isolationLevel);
        protected override DbCommand CreateDbCommand() => new OfflineCommand(source, this);
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class OfflineTransaction(DbConnection connection, IsolationLevel isolation) : DbTransaction
    {
        public int Commits, Rollbacks;
        public bool Disposed;
        public override IsolationLevel IsolationLevel => isolation;
        protected override DbConnection DbConnection => connection;
        public override void Commit() => Commits++;
        public override void Rollback() => Rollbacks++;
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class OfflineCommand(OfflineSource source, DbConnection connection) : DbCommand
    {
        private readonly NpgsqlCommand parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new NpgsqlParameter();
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar()
        {
            source.Commands.Add((CommandText, Transaction));
            if (CommandText.Contains("to_regclass")) return source.Available;
            if(CommandText.StartsWith("select etapa_funcional",StringComparison.Ordinal))
                return Equals(Parameters["integracao_id"].Value,source.Current.IntegracaoId)&&source.Row is not null?source.Row["etapa_funcional"]:null;
            if (!source.Valid(this)) return null;
            if (CommandText.Contains("for update")) { if (source.ExpireAfterLock) source.Expired = true; return source.Current.IntegracaoId; }
            if (source.ThrowUpsert) throw new InvalidOperationException("OFFLINE_UPSERT_FAILURE");
            source.Row = Parameters.Cast<DbParameter>().Where(p => p.ParameterName is not
                ("tentativa_id" or "fencing_token" or "worker" or "correlation_id" or "operacao"))
                .ToDictionary(p => p.ParameterName, p => p.Value!);
            source.RowCount = 1;
            if (source.Created == 0) source.Created = ++source.Updated;
            source.Updated++;
            return source.Current.IntegracaoId;
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var table = new DataTable();
            if (source.Row is not null && Equals(Parameters["correlation_id"].Value, source.Current.CorrelationId))
            {
                foreach (var (name, value) in source.Row) table.Columns.Add(name, value is DBNull ? typeof(string) : value.GetType());
                table.Rows.Add(source.Row.Values.ToArray());
            }
            return table.CreateDataReader();
        }
        protected override void Dispose(bool disposing) { parameters.Dispose(); base.Dispose(disposing); }
    }
}
