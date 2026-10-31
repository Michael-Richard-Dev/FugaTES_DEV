using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace R165R.Provider.Tests;

public class FactoryTests
{
    internal static void AssertPhysicalInitializer(NpgsqlDataSource source, bool expected = true)
    {
        // Npgsql 10.0.3 exposes no public inspection API. Inspect delegates, never invoke them.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var sync = typeof(NpgsqlDataSource).GetProperty("ConnectionInitializer", flags);
        var asyncInitializer = typeof(NpgsqlDataSource).GetProperty("ConnectionInitializerAsync", flags);
        Assert.NotNull(sync); Assert.NotNull(asyncInitializer);
        if (expected)
        {
            Assert.IsType<Action<NpgsqlConnection>>(sync.GetValue(source));
            Assert.IsType<Func<NpgsqlConnection, Task>>(asyncInitializer.GetValue(source));
        }
        else
        {
            Assert.Null(sync.GetValue(source)); Assert.Null(asyncInitializer.GetValue(source));
        }
    }

    private static NpgsqlDataSource CreateAcm(string? connection)
    {
        // Assert a missing R161L API as a runnable RED, rather than a compile failure.
        var method = typeof(NpgsqlDataSourceFactory).GetMethod("CriarAcm", new[] { typeof(string) });
        Assert.NotNull(method);
        return method.CreateDelegate<Func<string?, NpgsqlDataSource>>()(connection);
    }

    [Fact]
    public void Explicit_ACM_source_builds_without_opening_a_connection()
    {
        using var source = CreateAcm(ProviderConfigurationTests.Connection);
        Assert.IsAssignableFrom<NpgsqlDataSource>(source);
        AssertPhysicalInitializer(source);
    }

    [Theory]
    [InlineData(null)] [InlineData("")]
    [InlineData("Host=localhost;Database=fugates_dev;Username=fugates_dev_app;Search Path=fugates")]
    public void Explicit_ACM_factory_rejects_missing_or_legacy_metadata(string? connection) =>
        Assert.Throws<InvalidOperationException>(() => CreateAcm(connection));

    [Theory]
    [InlineData("Database", "other")]
    [InlineData("Search Path", "public")]
    [InlineData("Host", "remote.invalid")]
    [InlineData("Passfile", "")]
    [InlineData("Passfile", "/home/michael/.fugates-r116-dev/pgpass")]
    [InlineData("Password", "SYNTHETIC-REJECTED-NOT-A-CREDENTIAL")]
    public void Normal_factory_always_routes_new_role_to_guarded_metadata_checks(string key, string value)
    {
        var c = new NpgsqlConnectionStringBuilder(ProviderConfigurationTests.Connection); c[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => NpgsqlDataSourceFactory.Criar(c.ConnectionString));
        Assert.DoesNotContain("SYNTHETIC-REJECTED", error.ToString());
        Assert.DoesNotContain("/home/michael", error.ToString());
    }

    [Fact]
    public void Legacy_normal_factory_remains_usable_offline()
    {
        using var source = NpgsqlDataSourceFactory.Criar("Host=127.0.0.1;Port=5432;Database=fugates_dev;Username=fugates_dev_app");
        Assert.IsAssignableFrom<NpgsqlDataSource>(source);
        AssertPhysicalInitializer(source, expected: false);
    }

    [Theory]
    [InlineData("localhost")] [InlineData("127.0.0.1")]
    public void Callback_style_registration_of_new_role_builds_offline(string host)
    {
        var c = new NpgsqlConnectionStringBuilder(ProviderConfigurationTests.Connection) { Host = host };
        var services = new ServiceCollection();
        services.TryAddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSourceFactory.Criar(c.ConnectionString));
        using var provider = services.BuildServiceProvider();
        AssertPhysicalInitializer(provider.GetRequiredService<NpgsqlDataSource>());
    }

    [Theory]
    [InlineData("fugates_dev_app")] [InlineData("postgres")]
    public void Explicit_DI_ACM_binding_never_falls_back_to_normal_factory(string role)
    {
        var c = new NpgsqlConnectionStringBuilder(ProviderConfigurationTests.Connection) { Username = role };
        var services = new ServiceCollection();
        services.AddRepositorioMotorIntegracaoPostgreSql(c.ConnectionString, requireAcmPhysicalIdentity: true);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<NpgsqlDataSource>());
    }

    [Theory]
    [InlineData(5432)] [InlineData(5434)]
    public void Both_factories_reject_wrong_port_before_any_authentication(int port)
    {
        var c = new NpgsqlConnectionStringBuilder(ProviderConfigurationTests.Connection) { Port = port };
        Assert.Throws<InvalidOperationException>(() => CreateAcm(c.ConnectionString));
        Assert.Throws<InvalidOperationException>(() => NpgsqlDataSourceFactory.Criar(c.ConnectionString));
    }

    [Theory]
    [InlineData("relative/pgpass")]
    [InlineData("../pgpass")]
    [InlineData("~/pgpass")]
    [InlineData("C:\\SYNTHETIC-NOT-ACCESSED\\passfile")]
    [InlineData("/SYNTHETIC-NOT-ACCESSED/pass;file")]
    [InlineData("/SYNTHETIC-NOT-ACCESSED/pass\nfile")]
    [InlineData("//SYNTHETIC-NOT-ACCESSED/passfile")]
    [InlineData("/home/michael/.fugates-r116-dev/pgpass")]
    public void Both_factories_reject_non_absolute_or_unsafe_or_legacy_passfile(string passfile)
    {
        var c = new NpgsqlConnectionStringBuilder(ProviderConfigurationTests.Connection) { Passfile = passfile };
        Assert.Throws<InvalidOperationException>(() => CreateAcm(c.ConnectionString));
        Assert.Throws<InvalidOperationException>(() => NpgsqlDataSourceFactory.Criar(c.ConnectionString));
    }
}
