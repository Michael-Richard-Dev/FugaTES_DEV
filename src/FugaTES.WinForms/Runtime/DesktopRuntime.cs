using System.Net.Sockets;
using Npgsql;

namespace FugaTES.WinForms.Runtime;

public sealed class DesktopRuntime : IDisposable
{
    public static DesktopRuntime? Current { get; private set; }
    public static string EnvironmentDisplay { get; private set; } = "Não validado";
    public static string? FailureCode { get; private set; }
    public static bool Connected => Current is not null;
    private NpgsqlDataSource? dataSource;
    private DesktopTunnel? tunnel;
    private string? connectionString;
    public bool TunnelCreated => tunnel?.OwnsProcess == true;
    public NpgsqlDataSource CreateReadSource() => NpgsqlDataSource.Create(connectionString!);

    /// <summary>
    /// Fonte de ESCRITA. A conexao padrao do desktop e somente leitura por configuracao; aqui a
    /// leitura obrigatoria e desligada para a transacao, e a identidade e conferida antes - banco
    /// e usuario tem de ser os esperados, para uma configuracao trocada nao virar escrita no lugar
    /// errado. A conferencia e repetida em CreateCarrierStore de proposito: unificar as duas
    /// mudaria os codigos de erro de um caminho que ja esta em producao.
    /// </summary>
    public NpgsqlDataSource CreateWriteSource(string applicationName)
    {
        if (connectionString is null) throw new DesktopStartupException("DESKTOP_DATABASE_NOT_READY");
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        { Options = "-c default_transaction_read_only=off", ApplicationName = applicationName };
        if (options.Database != "fugates_dev" || options.Username != "fugates_dev_app")
            throw new DesktopStartupException("DESKTOP_DATABASE_IDENTITY_INVALID");
        return NpgsqlDataSource.Create(options.ConnectionString);
    }

    public FugaTES.Persistencia.Transportadoras.CarrierSyncPostgreSqlStore CreateCarrierStore(out NpgsqlDataSource ownedSource)
    {
        if(connectionString is null)throw new DesktopStartupException("CARRIER_DESKTOP_DATABASE_NOT_READY");
        var options=new NpgsqlConnectionStringBuilder(connectionString){Options="-c default_transaction_read_only=off",ApplicationName="FugaTES.Desktop.Carrier.HML"};
        if(options.Database!="fugates_dev"||options.Username!="fugates_dev_app")throw new DesktopStartupException("CARRIER_DESKTOP_DATABASE_IDENTITY_INVALID");
        var carrierConnection=options.ConnectionString;
        ownedSource=NpgsqlDataSource.Create(carrierConnection);
        return new(ownedSource,carrierConnection);
    }

    public static void Initialize()
    {
        try { Current = BootstrapAsync().GetAwaiter().GetResult(); }
        catch (DesktopStartupException error) { FailureCode = error.Code; }
        catch { FailureCode = "DESKTOP_STARTUP_FAILED"; }
    }

    private static async Task<DesktopRuntime> BootstrapAsync()
    {
        var config = DesktopConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "FugaTES.runtime.json"));
        EnvironmentDisplay = config.Environment == "HML" ? "HML" : "Não validado";
        config.Validar();
        var password = WindowsCredentialProvider.ReadPassword(config.Database.CredentialTarget, config.Database.Username);
        var runtime = new DesktopRuntime { connectionString = config.ConnectionString(password) };
        runtime.dataSource = NpgsqlDataSource.Create(runtime.connectionString);
        try
        {
            if (await PortAvailableAsync(config.Database.Port))
            {
                // Reuse only after validating actual PostgreSQL authentication and read-only identity.
                await runtime.ValidateDatabaseAsync(config);
                return runtime;
            }
            runtime.tunnel = DesktopTunnel.Start(config);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(config.ReadinessSeconds);
            while (!await PortAvailableAsync(config.Database.Port))
            {
                if (runtime.tunnel.HasExited) throw new DesktopStartupException("DESKTOP_SSH_PROCESS_EXITED");
                if (DateTimeOffset.UtcNow >= deadline) throw new DesktopStartupException("DESKTOP_SSH_READINESS_TIMEOUT");
                await Task.Delay(200);
            }
            await runtime.ValidateDatabaseAsync(config);
            return runtime;
        }
        catch { runtime.Dispose(); throw; }
    }

    private async Task ValidateDatabaseAsync(DesktopConfiguration configuration)
    {
        try
        {
            await using var connection = await dataSource!.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "select current_database(), current_user, current_setting('transaction_read_only'), to_regnamespace(@schema) is not null;";
            command.Parameters.AddWithValue("schema", configuration.Database.Schema);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || reader.GetString(0) != configuration.Database.Database ||
                reader.GetString(1) != configuration.Database.Username || reader.GetString(2) != "on" || !reader.GetBoolean(3))
                throw new DesktopStartupException("DESKTOP_DATABASE_IDENTITY_INVALID");
        }
        catch (PostgresException error) { throw new DesktopStartupException("DESKTOP_DB_AUTH_OR_PERMISSION_" + error.SqlState); }
        catch (NpgsqlException) { throw new DesktopStartupException("DESKTOP_POSTGRESQL_CONNECTION_FAILED"); }
        catch (TimeoutException) { throw new DesktopStartupException("DESKTOP_POSTGRESQL_TIMEOUT"); }
    }

    private static async Task<bool> PortAvailableAsync(int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await client.ConnectAsync("127.0.0.1", port, timeout.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    public void Dispose()
    {
        try { dataSource?.Dispose(); }
        finally
        {
            dataSource = null;
            connectionString = null;
            tunnel?.Dispose();
            tunnel = null;
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }

    public static string FailureText => FailureCode switch
    {
        "DESKTOP_CONFIG_MISSING" => "Configuração local FugaTES.runtime.json não encontrada.",
        "DESKTOP_DATABASE_IDENTITY_MISSING" => "Perfil PostgreSQL HML ainda não instalado: banco, usuário e credencial segura.",
        "DESKTOP_DB_CREDENTIAL_MISSING" => "Credencial PostgreSQL HML não encontrada no Credential Manager do Windows.",
        "DESKTOP_SSH_CONFIG_MISSING" => "Perfil SSH dedicado ainda não configurado.",
        "DESKTOP_SSH_KEY_OR_HOST_TRUST_MISSING" => "Chave SSH ou registro de host confiável indisponível.",
        "DESKTOP_SSH_KEY_ACL_INVALID" => "Permissões da chave SSH precisam restringir o acesso ao usuário autorizado.",
        _ => "Startup não concluído. Código técnico: " + (FailureCode ?? "DESKTOP_NOT_INITIALIZED")
    };
}