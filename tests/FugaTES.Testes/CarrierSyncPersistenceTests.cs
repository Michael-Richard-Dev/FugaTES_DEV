using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Persistencia.Transportadoras;
namespace FugaTES.Testes;
public sealed class CarrierSyncPersistenceTests
{
    [Theory][InlineData("token=secret")][InlineData("http://unsafe")][InlineData("raw payload")]
    public void Reject_sensitive_or_unstructured_log_code(string code) => Assert.Throws<ArgumentException>(() => CarrierPersistencePolicy.ValidateCode(code));
    [Fact] public void Known_step_and_technical_code_are_accepted() { CarrierPersistencePolicy.ValidateCode("GUARDIAN_TIMEOUT_INDETERMINATE"); CarrierPersistencePolicy.ValidateStep("GUARDIAN_READBACK"); }
    [Fact] public void Unknown_step_is_rejected() => Assert.Throws<ArgumentException>(() => CarrierPersistencePolicy.ValidateStep("BODY"));
    [Fact] public void Expired_or_future_heartbeat_is_not_active() { var now = DateTimeOffset.UtcNow; Assert.False(CarrierPersistencePolicy.ServiceActive(true,now.AddSeconds(-66),now)); Assert.False(CarrierPersistencePolicy.ServiceActive(true,now.AddSeconds(1),now)); Assert.False(CarrierPersistencePolicy.ServiceActive(false,now,now)); Assert.True(CarrierPersistencePolicy.ServiceActive(true,now.AddSeconds(-30),now)); }
    [Fact] public void Filters_are_bounded() { Assert.Throws<ArgumentException>(() => new CarrierSyncFiltro(Limit:1001).Validar()); Assert.Throws<ArgumentException>(() => new CarrierSyncFiltro(DeUtc:DateTimeOffset.UtcNow,AteUtc:DateTimeOffset.UtcNow.AddDays(-1)).Validar()); }
    [Fact] public void Sql_reservation_blocks_pending_and_indeterminate_attempt() { Assert.Contains("pending_attempt_id is null",CarrierSyncPostgreSqlStore.ReserveSql,StringComparison.OrdinalIgnoreCase); Assert.Contains("INDETERMINATE",CarrierSyncPostgreSqlStore.ReserveSql); Assert.Contains("WRITE_IN_FLIGHT",CarrierSyncPostgreSqlStore.ReserveSql); }
    [Fact] public async Task Database_failure_never_grants_lease()
    {
        await using var source=Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=local_test;Database=local_test;Timeout=1");
        var store=new CarrierSyncPostgreSqlStore(source,"Host=127.0.0.1;Port=1;Username=local_test;Database=local_test;Timeout=1");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<Exception>(()=>store.TentarAdquirirCicloAsync(timeout.Token));
    }
    [Fact] public void Dedicated_connection_preserves_fake_authentication_configuration()
    {
        using var source=Npgsql.NpgsqlDataSource.Create("Host=localhost;Username=fixture;Database=fixture;Password=fixture_password");
        Assert.Null(CarrierSyncPostgreSqlStore.LockConnectionOptions(source.ConnectionString).Password);
        Assert.Equal("fixture_password",CarrierSyncPostgreSqlStore.LockConnectionOptions("Host=localhost;Username=fixture;Database=fixture;Password=fixture_password").Password);
    }
    [Fact] public void Durable_visited_pages_preserve_restart_loop_detection() { var checkpoint=new CarrierStoredCheckpoint(null,DateTimeOffset.UtcNow,"page_b") { VisitedPages=[new string('A',64),new string('B',64)] }; var restored=System.Text.Json.JsonSerializer.Deserialize<CarrierStoredCheckpoint>(System.Text.Json.JsonSerializer.Serialize(checkpoint)); Assert.Equal(checkpoint.VisitedPages,restored!.VisitedPages); }
    [Fact] public void Explicit_unpooled_lock_connection() { Assert.False(CarrierSyncPostgreSqlStore.LockConnectionOptions("Host=localhost;Username=test;Database=test").Pooling); }
}




