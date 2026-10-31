using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Persistencia.MotorIntegracao;
using Xunit;

namespace R165R.Provider.Tests;

public class PhysicalIdentityTests
{
    [Fact]
    public async Task New_role_on_exact_physical_identity_is_accepted_and_transaction_rolled_back()
    {
        using var c = new IdentityConnection();
        await AcmPostgreSqlIdentityGuard.VerifyAsync(c);
        Assert.True(c.RolledBack);
        Assert.Equal("set transaction read only", c.Commands[0]);
        Assert.Contains("current_setting('port')", c.Commands[1]);
        Assert.Contains("session_user", c.Commands[1]);
        Assert.Equal(2, c.Commands.Count);
    }

    [Theory]
    [InlineData("cluster")] [InlineData("port5432")] [InlineData("port5434")]
    [InlineData("inet_port")] [InlineData("database")] [InlineData("schema")]
    [InlineData("old_current_user")] [InlineData("old_session_user")] [InlineData("recovery")]
    [InlineData("empty_result")] [InlineData("null_cluster")]
    public async Task Physical_mismatch_is_blocked_even_if_connection_metadata_looks_correct(string field)
    {
        using var c = new IdentityConnection();
        switch (field)
        {
            case "cluster": c.Cluster = "18/main"; break;
            case "port5432": c.Port = 5432; break;
            case "port5434": c.Port = 5434; break;
            case "inet_port": c.InetPort = 5432; break;
            case "database": c.DatabaseName = "other"; break;
            case "schema": c.Schema = "public"; break;
            case "old_current_user": c.Role = "fugates_dev_app"; break;
            case "old_session_user": c.Session = "fugates_dev_app"; break;
            case "recovery": c.Recovery = true; break;
            case "empty_result": c.Empty = true; break;
            case "null_cluster": c.Cluster = null; break;
        }
        await Assert.ThrowsAsync<AcmR57BlockedException>(() => AcmPostgreSqlIdentityGuard.VerifyAsync(c));
        Assert.True(c.RolledBack);
    }

    [Theory]
    [InlineData("begin")] [InlineData("readonly")] [InlineData("query")] [InlineData("rollback")]
    public async Task Driver_failures_never_expose_connection_or_secret_details(string stage)
    {
        using var c = new IdentityConnection { Failure = stage };
        var error = await Assert.ThrowsAsync<AcmR57BlockedException>(() => AcmPostgreSqlIdentityGuard.VerifyAsync(c));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE", error.ToString());
        if (stage != "begin") Assert.True(c.RolledBack);
    }

    [Theory]
    [InlineData("valid")][InlineData("old_current_user")][InlineData("old_session_user")]
    [InlineData("owner")][InlineData("postgres")][InlineData("unknown")]
    [InlineData("cluster")][InlineData("port5432")][InlineData("port5434")]
    [InlineData("inet_port")][InlineData("database")][InlineData("schema")][InlineData("recovery")]
    public async Task One_shot_verifies_dedicated_physical_identity_before_dispatch(string field)
    {
        using var c=new IdentityConnection();
        switch(field)
        {
            case "old_current_user":c.Role="fugates_dev_app";break;
            case "old_session_user":c.Session="fugates_dev_app";break;
            case "owner":c.Role=c.Session="fugates_dev_owner";break;
            case "postgres":c.Role=c.Session="postgres";break;
            case "unknown":c.Role=c.Session="unapproved_app";break;
            case "cluster":c.Cluster="18/main";break;
            case "port5432":c.Port=5432;break;
            case "port5434":c.Port=5434;break;
            case "inet_port":c.InetPort=5432;break;
            case "database":c.DatabaseName="other";break;
            case "schema":c.Schema="public";break;
            case "recovery":c.Recovery=true;break;
        }
        var method=typeof(FugaTES.HostIntegracao.AcmR104OneShotEntrypoint).GetMethod(
            "VerifyDatabaseIdentityAsync",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic,
            null,new[]{typeof(DbConnection),typeof(CancellationToken)},null);
        Assert.NotNull(method);
        Task Verify()=> (Task)method.Invoke(null,new object[]{c,CancellationToken.None})!;
        if(field=="valid")await Verify();
        else await Assert.ThrowsAsync<AcmR57BlockedException>(Verify);
        Assert.True(c.RolledBack);
        Assert.Contains("cluster_name",c.Commands[1]);
    }

    // Only the ADO boundary is fake. VerifyAsync and its SQL/transaction lifecycle are real.
    private sealed class IdentityConnection : DbConnection
    {
        public string? Cluster = "18/fugatesacmdev";
        public string DatabaseName = "fugates_dev", Schema = "fugates", Role = "fugates_acm_hml_app", Session = "fugates_acm_hml_app";
        public int Port = 5433;
        public int? InetPort = 5433;
        public bool Recovery, Empty, RolledBack;
        public string? Failure;
        public List<string> Commands = [];
        [AllowNull] public override string ConnectionString { get; set; } = ProviderConfigurationTests.Connection;
        public override string Database => DatabaseName;
        public override string DataSource => "SYNTHETIC";
        public override string ServerVersion => "18";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() { }
        public override void Open() => throw new InvalidOperationException("No real connection permitted");
        public void FailAt(string stage)
        {
            if (Failure == stage) throw new InvalidOperationException("SYNTHETIC_PRIVATE_DRIVER_DETAIL");
        }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            FailAt("begin"); return new IdentityTransaction(this);
        }
        protected override DbCommand CreateDbCommand() => new IdentityCommand(this);

        private sealed class IdentityTransaction(IdentityConnection connection) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            protected override DbConnection DbConnection => connection;
            public override void Commit() => throw new InvalidOperationException("Commit forbidden");
            public override void Rollback() { connection.RolledBack = true; connection.FailAt("rollback"); }
        }

        private sealed class IdentityCommand(IdentityConnection connection) : DbCommand
        {
            [AllowNull] public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = connection;
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
            public override void Cancel() { }
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
            private void Record()
            {
                Assert.NotNull(DbTransaction);
                Assert.Equal(5, CommandTimeout);
                connection.Commands.Add(CommandText);
            }
            public override int ExecuteNonQuery()
            {
                Record(); connection.FailAt("readonly"); return 0;
            }
            public override object? ExecuteScalar() => throw new NotSupportedException();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                Record(); connection.FailAt("query");
                var table = new DataTable();
                foreach (var (name, type) in new[] { ("cluster", typeof(string)), ("port", typeof(int)), ("inet_port", typeof(int)), ("database", typeof(string)), ("schema", typeof(string)), ("role", typeof(string)), ("session", typeof(string)), ("recovery", typeof(bool)) })
                    table.Columns.Add(name, type);
                if (!connection.Empty)
                    table.Rows.Add((object?)connection.Cluster ?? DBNull.Value, connection.Port, (object?)connection.InetPort ?? DBNull.Value,
                        connection.DatabaseName, connection.Schema, connection.Role, connection.Session, connection.Recovery);
                return table.CreateDataReader();
            }
        }
    }
}
