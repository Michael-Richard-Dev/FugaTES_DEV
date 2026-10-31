using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Persistencia.Transportadoras;
using Npgsql;

namespace FugaTES.Testes;

public sealed class CarrierLocalPostgreSqlFactAttribute : FactAttribute
{
    public CarrierLocalPostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FUGATES_CARRIER_TEST_PG_PROFILE")))
            Skip = "PostgreSQL local isolado indisponivel; execucao real de SQL nao comprovada.";
    }
}

public class CarrierMigrationPostgreSqlTests
{
    private sealed record TestProfile(string AdminConnectionString, string AppConnectionString);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static string Sql(string name)
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,"FugaTES.slnx"))) directory=directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new InvalidOperationException("REPO_ROOT_MISSING"),"docs","Incrementos","007",name+"_007_CARRIER_SYNC_R3.sql"));
    }
    private static void RequireIsolatedLocal(NpgsqlConnectionStringBuilder options)
    {
        if (options.Host is not ("localhost" or "127.0.0.1" or "::1") || !(options.Database?.StartsWith("fugates_carrier_test_",StringComparison.Ordinal) ?? false)
            || string.IsNullOrWhiteSpace(options.Username)) throw new InvalidOperationException("ISOLATED_LOCAL_TEST_DATABASE_REQUIRED");
    }
    private static async Task Execute(NpgsqlConnection connection,string sql)
    {await using var command=new NpgsqlCommand(sql,connection);await command.ExecuteNonQueryAsync(Ct);}
    private static async Task<object?> Scalar(NpgsqlConnection connection,string sql)
    {await using var command=new NpgsqlCommand(sql,connection);return await command.ExecuteScalarAsync(Ct);}
    private static async Task AssertRollbackBlocked(NpgsqlConnection admin,string expected)
    {
        var exception=await Assert.ThrowsAsync<PostgresException>(()=>Execute(admin,Sql("ROLLBACK")));
        Assert.Equal(expected,exception.MessageText);
        await Execute(admin,"ROLLBACK");
        Assert.Equal(true,await Scalar(admin,"select to_regclass('fugates.carrier_sync_checkpoint') is not null"));
    }

    [CarrierLocalPostgreSqlFact]
    public async Task Real_role_access_checkpoint_reconciliation_and_preoperational_rollback()
    {
        // Secrets stay in an external local profile. This fixture rejects HML/PRD and remote hosts.
        var profile=JsonSerializer.Deserialize<TestProfile>(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("FUGATES_CARRIER_TEST_PG_PROFILE")!,Ct))
            ?? throw new InvalidOperationException("TEST_PROFILE_INVALID");
        var adminOptions=new NpgsqlConnectionStringBuilder(profile.AdminConnectionString);
        var appOptions=new NpgsqlConnectionStringBuilder(profile.AppConnectionString);
        RequireIsolatedLocal(adminOptions);RequireIsolatedLocal(appOptions);
        Assert.Equal(adminOptions.Database,appOptions.Database);Assert.Equal(adminOptions.Host,appOptions.Host);Assert.Equal(adminOptions.Port,appOptions.Port);
        await using var admin=new NpgsqlConnection(profile.AdminConnectionString);await admin.OpenAsync(Ct);
        await using var app=new NpgsqlConnection(profile.AppConnectionString);await app.OpenAsync(Ct);
        Assert.Equal(appOptions.Username,await Scalar(app,"select current_user"));
        Assert.Equal(appOptions.Database,await Scalar(app,"select current_database()"));
        Assert.Equal(true,await Scalar(admin,"select to_regnamespace('fugates') is not null and to_regclass('fugates.carrier_sync_checkpoint') is null"));
        await using(var settings=new NpgsqlCommand("select set_config('fugates.carrier_app_role',@role,false),set_config('fugates.carrier_profile_role',@role,false),set_config('fugates.carrier_role_approval','ISOLATED_TEST_ONLY',false)",admin))
        {settings.Parameters.AddWithValue("role",appOptions.Username!);await settings.ExecuteNonQueryAsync(Ct);}
        await Execute(admin,Sql("MIGRATION"));
        await Execute(admin,Sql("VALIDATION"));
        await AssertRollbackBlocked(admin,"CARRIER_ROLLBACK_WORKER_NOT_DISABLED");
        await Execute(admin,"select set_config('fugates.carrier_worker_disabled','true',false)");
        await Execute(admin,"update fugates.carrier_sync_checkpoint set watermark=now()");
        await AssertRollbackBlocked(admin,"CARRIER_ROLLBACK_OPERATIONAL_DATA");
        await Execute(admin,"update fugates.carrier_sync_checkpoint set watermark=null");
        await Execute(admin,Sql("ROLLBACK")); // Empty, original seed, worker off: permitted before operation.
        Assert.Equal(true,await Scalar(admin,"select to_regclass('fugates.carrier_sync_checkpoint') is null"));
        await Execute(admin,Sql("MIGRATION"));
        await using var source=NpgsqlDataSource.Create(profile.AppConnectionString);
        var store=new CarrierSyncPostgreSqlStore(source,profile.AppConnectionString);
        var now=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var next=new CarrierStoredCheckpoint(now,now.AddSeconds(1),"next-page");
        var hash=new string('a',64);
        var old=new CarrierStoredBp("OLD_BP","Synthetic",null,hash,"FAILED","NO_ACTION","FAILED",now,null,null);
        var clean=new CarrierStoredBp("CURRENT_BP","Synthetic",null,hash,"SYNCED","NO_ACTION","OK",now,now,null);
        foreach(var status in new[]{"FAILED","BLOCKED"})
        {
            await using var lease=await store.TentarAdquirirCicloAsync(Ct) ?? throw new InvalidOperationException("TEST_LEASE_MISSING");
            await lease.ConcluirBpAsync(old with {Status=status,ResultCode=status},Ct);
            await lease.ConcluirBpAsync(clean,Ct); // Current page clean; previous cycle remains unreconciled.
            var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>lease.ConfirmarPaginaAsync(next,Ct));
            Assert.Equal("CARRIER_CHECKPOINT_UNRECONCILED_OR_MISSING",failure.Message);
            Assert.Null((await store.CarregarCheckpointAsync(Ct)).Watermark);
        }
        await using(var lease=await store.TentarAdquirirCicloAsync(Ct) ?? throw new InvalidOperationException("TEST_LEASE_MISSING"))
        {
            await AssertRollbackBlocked(admin,"CARRIER_ROLLBACK_ACTIVE_LEASE");
            await lease.ConcluirBpAsync(old with {Status="SYNCED",ResultCode="OK",GuardianSyncedUtc=now},Ct);
            await lease.ConfirmarPaginaAsync(next,Ct);
            var version=await Scalar(app,"select xmin::text from fugates.carrier_sync_checkpoint where singleton");
            Assert.Equal(now,(await store.CarregarCheckpointAsync(Ct)).Watermark);
            await lease.ConfirmarPaginaAsync(next,Ct);
            Assert.Equal(version,await Scalar(app,"select xmin::text from fugates.carrier_sync_checkpoint where singleton"));
            var cycle=Guid.NewGuid();await lease.IniciarAsync(cycle,now,Ct);
            await lease.RegistrarEtapaAsync(new(cycle,"CURRENT_BP","SAP_GET","OK",now),Ct); // Identity INSERT using actual app login.
            var attempt=Guid.NewGuid();Assert.True(await lease.RegistrarAntesDoWriteAsync(new(attempt,cycle,"CURRENT_BP","CREATE",hash,null,now),Ct));
            await lease.ConcluirBpAsync(clean with {GuardianSyncedUtc=now.AddSeconds(1)},Ct);
            Assert.True(await store.EnfileirarPedidoAsync(Guid.NewGuid(),"CURRENT_BP","SYNTHETIC_TEST_SID",now,Ct));
            var request=await lease.TomarPedidoAsync(Ct);Assert.NotNull(request);await lease.ConcluirPedidoAsync(request.Id,"OK",Ct);
            await lease.ConcluirAsync(new(cycle,now,now.AddSeconds(1),1,1,0,0,0,0,"COMPLETED"),Ct);
            await Execute(app,Sql("VALIDATION")); // Real SELECT access on all six tables.
            Assert.Equal(false,await Scalar(app,"select has_schema_privilege(current_user,'fugates','CREATE') or has_sequence_privilege(current_user,'fugates.carrier_sync_step_id_seq','USAGE')"));
            Assert.Equal(false,await Scalar(app,"select has_table_privilege(current_user,'fugates.carrier_sync_state','DELETE') or has_table_privilege(current_user,'fugates.carrier_sync_state','TRUNCATE')"));
        }
        await AssertRollbackBlocked(admin,"CARRIER_ROLLBACK_OPERATIONAL_DATA");
        // Preserve test history, too. Provision another empty disposable database for a subsequent run.
    }
}