using FugaTES.WinForms.Runtime;
using Npgsql;

namespace FugaTES.Testes;

public sealed class DesktopZeroConfigRuntimeTests
{
    internal static DesktopConfiguration Valid() => new("HML", false, 20,
        new("127.0.0.1", 15432, "synthetic_hml", "synthetic_reader", "fugates", "FugaTES/HML/PostgreSQL"),
        new(false, "192.168.17.40", 22, "synthetic_ssh", @"C:\ExternalSecrets\key", @"C:\ExternalSecrets\known_hosts", "127.0.0.1", 5432));

    [Fact]
    public void Permanent_configuration_does_not_require_environment_variables() => Valid().Validar();

    [Theory]
    [InlineData("Production", false, "127.0.0.1", "DESKTOP_ENVIRONMENT_INVALID")]
    [InlineData("HML", true, "127.0.0.1", "DESKTOP_WORKER_FORBIDDEN")]
    [InlineData("HML", false, "192.168.17.40", "DESKTOP_DATABASE_ENDPOINT_INVALID")]
    public void Unsafe_startup_context_is_blocked(string environment, bool worker, string host, string reason)
    {
        var config = Valid() with { Environment = environment, IntegrationWorkerEnabled = worker,
            Database = Valid().Database with { Host = host } };
        Assert.Equal(reason, Assert.Throws<DesktopStartupException>(config.Validar).Code);
    }

    [Fact]
    public void Ssh_is_hidden_noninteractive_and_uses_only_external_key_paths()
    {
        var config = Valid() with { Ssh = Valid().Ssh with { Enabled = true } };
        var info = DesktopTunnel.StartInfo(config, @"C:\Windows\System32\OpenSSH\ssh.exe");
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.Contains("BatchMode=yes", info.ArgumentList);
        Assert.Contains("PasswordAuthentication=no", info.ArgumentList);
        Assert.Contains("StrictHostKeyChecking=yes", info.ArgumentList);
        Assert.Contains("ExitOnForwardFailure=yes", info.ArgumentList);
        Assert.Contains("127.0.0.1:15432:127.0.0.1:5432", info.ArgumentList);
        Assert.DoesNotContain("SYNTHETIC_PASSWORD", string.Join(" ", info.ArgumentList));
    }

    [Fact]
    public void Shutdown_without_owned_process_is_idempotent()
    {
        var lease = new DesktopTunnel();
        Assert.False(lease.OwnsProcess);
        lease.Dispose();
        lease.Dispose();
        Assert.False(lease.OwnsProcess);
    }

    [Fact]
    public void Private_key_inside_the_package_is_blocked()
    {
        var config = Valid() with { Ssh = Valid().Ssh with { Enabled = true,
            PrivateKeyPath = Path.Combine(AppContext.BaseDirectory, "never-created-test-key") } };
        Assert.Equal("DESKTOP_SSH_KEY_IN_PACKAGE", Assert.Throws<DesktopStartupException>(() => DesktopTunnel.Start(config)).Code);
    }

    [Fact]
    public void Configuration_file_rejects_accidental_plaintext_secret_fields()
    {
        var path = Path.GetTempFileName();
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Valid()))!;
            json["Password"] = "SYNTHETIC_PASSWORD";
            File.WriteAllText(path, json.ToJsonString());
            Assert.Equal("DESKTOP_CONFIG_INVALID", Assert.Throws<DesktopStartupException>(() => DesktopConfiguration.Load(path)).Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Database_session_is_read_only_and_password_is_not_configuration()
    {
        var config = Valid();
        var connection = new NpgsqlConnectionStringBuilder(config.ConnectionString("SYNTHETIC_PASSWORD"));
        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal(15432, connection.Port);
        Assert.Equal("-c default_transaction_read_only=on", connection.Options);
        Assert.False(connection.IncludeErrorDetail);
        Assert.Equal("SYNTHETIC_PASSWORD", connection.Password);
        Assert.DoesNotContain("SYNTHETIC_PASSWORD", System.Text.Json.JsonSerializer.Serialize(config));
    }
}