using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace FugaTES.WinForms.Runtime;

public sealed class DesktopStartupException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record DesktopDatabaseConfiguration(string Host, int Port, string Database, string Username,
    string Schema, string CredentialTarget);
public sealed record DesktopSshConfiguration(bool Enabled, string Host, int Port, string Username,
    string PrivateKeyPath, string KnownHostsPath, string RemoteDatabaseHost, int RemoteDatabasePort);
public sealed record DesktopConfiguration(string Environment, bool IntegrationWorkerEnabled,
    int ReadinessSeconds, DesktopDatabaseConfiguration Database, DesktopSshConfiguration Ssh)
{
    public static DesktopConfiguration Load(string path)
    {
        if (!File.Exists(path)) throw new DesktopStartupException("DESKTOP_CONFIG_MISSING");
        try
        {
            return JsonSerializer.Deserialize<DesktopConfiguration>(File.ReadAllText(path), new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new DesktopStartupException("DESKTOP_CONFIG_INVALID");
        }
        catch (JsonException) { throw new DesktopStartupException("DESKTOP_CONFIG_INVALID"); }
    }

    public void Validar()
    {
        if (Environment != "HML") throw new DesktopStartupException("DESKTOP_ENVIRONMENT_INVALID");
        if (IntegrationWorkerEnabled) throw new DesktopStartupException("DESKTOP_WORKER_FORBIDDEN");
        if (Database is null || Database.Host != "127.0.0.1" || Database.Port is < 1 or > 65535)
            throw new DesktopStartupException("DESKTOP_DATABASE_ENDPOINT_INVALID");
        if (new[] { Database.Database, Database.Username, Database.CredentialTarget }.Any(string.IsNullOrWhiteSpace) || Database.Schema != "fugates")
            throw new DesktopStartupException("DESKTOP_DATABASE_IDENTITY_MISSING");
        if (ReadinessSeconds is < 1 or > 60 || Ssh is null)
            throw new DesktopStartupException("DESKTOP_CONFIG_INVALID");
        if (Ssh.Enabled && (new[] { Ssh.Host, Ssh.Username, Ssh.PrivateKeyPath, Ssh.KnownHostsPath, Ssh.RemoteDatabaseHost }.Any(string.IsNullOrWhiteSpace) ||
            Ssh.Port is < 1 or > 65535 || Ssh.RemoteDatabasePort is < 1 or > 65535 ||
            !Path.IsPathFullyQualified(Ssh.PrivateKeyPath) || !Path.IsPathFullyQualified(Ssh.KnownHostsPath)))
            throw new DesktopStartupException("DESKTOP_SSH_CONFIG_MISSING");
    }

    public string ConnectionString(string password)
    {
        Validar();
        if (string.IsNullOrEmpty(password)) throw new DesktopStartupException("DESKTOP_DB_CREDENTIAL_MISSING");
        return new NpgsqlConnectionStringBuilder
        {
            Host = Database.Host, Port = Database.Port, Database = Database.Database, Username = Database.Username,
            Password = password, Timeout = 3, CommandTimeout = 10, IncludeErrorDetail = false,
            ApplicationName = "FugaTES.Desktop.ReadOnly", Options = "-c default_transaction_read_only=on"
        }.ConnectionString;
    }
}