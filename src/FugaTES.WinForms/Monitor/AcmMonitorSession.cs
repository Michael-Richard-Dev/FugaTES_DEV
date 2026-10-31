using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.WinForms.Runtime;
using Npgsql;

namespace FugaTES.WinForms.Monitor;

public sealed record AcmMonitorProfile
{
    public bool Enabled { get; init; }
    public int LocalPort { get; init; }
    public string CredentialTarget { get; init; } = "";
    public string ClearanceReference { get; init; } = "";
    public string TransportSslMode { get; init; } = "";
    public string TransportPolicyReference { get; init; } = "";
    public bool ReadbackEnabled { get; init; }
    public string ReadbackFile { get; init; } = "";
    public Guid ReadbackIntegrationId { get; init; }
    public string ReadbackContract { get; init; } = "";
    public int ReadbackMaxAgeSeconds { get; init; }
    public GuardianProtocolProfile GuardianProtocol { get; init; } = new();
    public string ProtocolPreviewReadbackFile { get; init; } = "";

    public bool Valido => !ReadbackEnabled && Enabled && LocalPort == 15433
        && CredentialTarget == "FugaTES/HML/ACM/PostgreSQL" && !string.IsNullOrWhiteSpace(ClearanceReference)
        && !string.IsNullOrWhiteSpace(TransportPolicyReference)
        && TransportSslMode is "Disable" or "Require" or "VerifyCA" or "VerifyFull";
}

public sealed class AcmMonitorSession : IDisposable
{
    private readonly NpgsqlDataSource? source;
    private readonly MonitorPostgreSqlReadRepository? repository;
    private readonly MonitorReadbackRepository? readback;
    public bool LeituraExportada => readback is not null;
    public bool Configurado => source is not null || readback is not null;
    public MonitorOperacionalConsulta Consulta { get; }
    public string Estado => readback?.Estado ?? (Configurado ? "ACM HML 5433 - identidade fisica exigida" : "ACM HML - acesso desabilitado / configuracao pendente");

    private AcmMonitorSession(NpgsqlDataSource? source, MonitorReadbackRepository? readback = null)
    {
        this.source = source;
        this.readback = readback;
        repository = source is null ? null : new MonitorPostgreSqlReadRepository(source, exigirIdentidadeAcm: true);
        IMonitorReadRepository reader = readback is not null ? readback : repository is not null ? repository : new Indisponivel();
        Consulta = new(reader);
    }

    public Task<MonitorAcmDetalhe> DetalharAcmAsync(Guid id, CancellationToken ct)
        => readback?.DetalharAcmAsync(id, ct) ?? repository?.DetalharAcmAsync(id, ct) ?? throw new InvalidOperationException("ACM_MONITOR_CONFIG_REQUIRED");

    public static AcmMonitorSession Criar(AcmMonitorProfile profile, Func<string, string, string>? readPassword = null)
    {
        if (profile.ReadbackEnabled)
        {
            try { return new(null, new MonitorReadbackRepository(profile)); }
            catch { return new(null); }
        }
        if (!profile.Valido) return new(null);
        try
        {
            var password = (readPassword ?? WindowsCredentialProvider.ReadPassword)(profile.CredentialTarget, "fugates_acm_hml_app");
            if (string.IsNullOrEmpty(password)) return new(null);
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = profile.LocalPort, Database = "fugates_dev", Username = "fugates_acm_hml_app",
                Password = password, SearchPath = "fugates", Timeout = 5, CommandTimeout = 10,
                GssEncryptionMode = GssEncryptionMode.Disable, SslMode = Enum.Parse<SslMode>(profile.TransportSslMode),
                MaxPoolSize = 2, PersistSecurityInfo = false, IncludeErrorDetail = false,
                ApplicationName = "FugaTES.AcmMonitor.R165M.ReadOnly", Options = "-c default_transaction_read_only=on"
            };
            return new(NpgsqlDataSource.Create(builder.ConnectionString));
        }
        catch { return new(null); }
    }

    public static AcmMonitorSession Carregar(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "AcmMonitor.R165M.json");
            if (!File.Exists(path)) return new(null);
            var profile = JsonSerializer.Deserialize<AcmMonitorProfile>(File.ReadAllText(path),
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            return profile is null ? new(null) : Criar(profile);
        }
        catch { return new(null); }
    }

    public void Dispose() => source?.Dispose();

    private sealed class Indisponivel : IMonitorReadRepository
    {
        public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct) => throw new InvalidOperationException("ACM_MONITOR_CONFIG_REQUIRED");
        public Task<MonitorDetalhe> DetalharAsync(Guid integracaoId, CancellationToken ct) => throw new InvalidOperationException("ACM_MONITOR_CONFIG_REQUIRED");
    }
}
