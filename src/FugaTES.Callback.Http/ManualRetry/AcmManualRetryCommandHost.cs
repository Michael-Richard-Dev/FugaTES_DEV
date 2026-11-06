using FugaTES.Aplicacao.Configuracao;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ManualRetry;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.HostIntegracao;
using FugaTES.Persistencia.ManualRetry;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FugaTES.Callback.Http.ManualRetry;

public static class AcmManualRetryCommandHost
{
    public const string StartupFlag = "--acm-manual-retry-command";
    public static bool Selected(string[] args)
    {
        if (!args.Any(a => a.StartsWith(StartupFlag, StringComparison.Ordinal))) return false;
        if (args.Length != 1 || args[0] != StartupFlag) throw new InvalidOperationException("ACM_COMMAND_EXCLUSIVE_ARGUMENTS_REQUIRED");
        return true;
    }
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("ACM_COMMAND_LINUX_VM_REQUIRED");
        var profilePath = Environment.GetEnvironmentVariable("FUGATES_ACM_MANUAL_RETRY_PROFILE") ?? "";
        var profile = JsonSerializer.Deserialize<AcmManualRetryCommandProfile>(PrivateCommandFile.Read(profilePath, 16384), AcmManualRetryCommandProfile.Json)
            ?? throw new InvalidOperationException("ACM_COMMAND_PROFILE_REQUIRED");
        var approvals = JsonSerializer.Deserialize<AcmRetryApproval[]>(PrivateCommandFile.Read(profile.ApprovalsFile, 262144), AcmManualRetryCommandProfile.Json)
            ?? throw new InvalidOperationException("ACM_COMMAND_APPROVAL_LIST_REQUIRED");
        profile.Validate(approvals);
        var token = Encoding.ASCII.GetString(PrivateCommandFile.Read(profile.CredentialFile, 66)).TrimEnd('\r', '\n');
        var credential = new AcmCommandCredential(token, profile.OperatorSid);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders(); builder.Logging.AddConsole();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 18763));
        builder.Services.AddSingleton(profile);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        AcmCommandAuthenticationHandler.Configure(builder.Services, credential);
        builder.Services.AddSingleton<IAcmRetryApprovals>(new StaticAcmRetryApprovals(profile, approvals));
        if (approvals.Length != 0)
        {
            PrivateCommandFile.ValidateMetadata(profile.Passfile, 65536);
            var normal = JsonSerializer.Deserialize<AcmR57NormalOptions>(PrivateCommandFile.Read(profile.NormalConfigurationFile!, 65536), AcmManualRetryCommandProfile.Json)
                ?? throw new InvalidOperationException("ACM_COMMAND_NORMAL_EVIDENCE_REQUIRED");
            normal = ApproveNormal(profile, approvals[0], normal);
            // Read existing trusted runtime configuration; do not modify the source file.
            using var baseJson = new MemoryStream(PrivateCommandFile.Read(profile.BaseConfigurationFile, 1048576, privateContent: false));
            var runtime = BuildRuntimeConfiguration(profile, new ConfigurationBuilder().AddJsonStream(baseJson).Build());
            // Guardian credentials are used only by the original runtime adapter, never command authentication.
            // R163: prefer a restricted file (0600, no symlink, owner-checked, outside the release) so the
            // secret never reaches /proc/<pid>/environ. Falls back to the environment while the file is absent,
            // so behaviour is unchanged until the operator provisions it.
            foreach (var (name, value) in LerCredenciaisGuardian(profile))
                runtime["Guardian:" + name] = value;
            builder.Services.AddSingleton<IConfiguration>(runtime);
            ConfigureApprovedRuntime(builder.Services,runtime,normal);
            builder.Services.RemoveAll<IIdentidadeWorkerProvider>();
            builder.Services.AddSingleton<IIdentidadeWorkerProvider>(new FixedCommandWorker(profile.WorkerIdentity));
            builder.Services.AddScoped(sp=>new AcmManualRetryPostgreSql(sp.GetRequiredService<NpgsqlDataSource>(),AcmEnvironmentBinding.Banco(runtime)));
            builder.Services.AddScoped<IAcmManualRetryStore>(sp => new CommittedAcmRetryStore(sp.GetRequiredService<AcmManualRetryPostgreSql>(), sp.GetRequiredService<AcmRetryDispatchQueue>()));
            builder.Services.AddSingleton(_ => CreateDispatchQueue(profile, approvals));
            builder.Services.AddSingleton<IAcmApprovedRetryExecutor, AcmApprovedRetryExecutor>();
            builder.Services.AddHostedService<AcmRetryCommandWorker>();
        }
        else builder.Services.AddScoped<IAcmManualRetryStore, UnapprovedStore>();
        builder.Services.AddScoped<AcmManualRetryService>();
        var toledoRetry=ToledoSapCommandComposition.Configure(builder.Services,profile);
        var mmRetry=MmRetryCommandComposition.Configure(builder.Services,profile);
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        AcmManualRetryEndpoint.MapCandidate(app);
        if(toledoRetry)ToledoSapRetryEndpoint.MapCandidate(app);
        if(mmRetry)MmRetryEndpoint.MapCandidate(app);
        app.MapGet("/health", () => Results.Json(new { service = "ACM_MANUAL_RETRY_COMMAND", approvals = approvals.Length, scopedWorker = approvals.Length == 1 }))
            .RequireAuthorization(AcmManualRetryEndpoint.Policy);
        await app.RunAsync();
    }

    /// <summary>
    /// Credenciais Guardian do adaptador de runtime. Precedencia: arquivo restrito do perfil e,
    /// somente na ausencia dele, a variavel de ambiente herdada.
    ///
    /// O arquivo e lido por PrivateCommandFile com privateContent:true, que recusa modo acessivel
    /// a grupo/outros, symlink, dono inesperado e caminho dentro do release. Um arquivo presente
    /// porem invalido NAO cai silenciosamente para o ambiente: a excecao propaga, porque aceitar
    /// o caminho antigo nesse caso anularia a propria remediacao.
    /// </summary>
    public static IReadOnlyList<(string Nome, string Valor)> LerCredenciaisGuardian(AcmManualRetryCommandProfile profile)
    {
        var nomes = new[] { "Email", "Password", "Signature" };
        if (!string.IsNullOrWhiteSpace(profile.GuardianSecretsFile))
        {
            var conteudo = PrivateCommandFile.Read(profile.GuardianSecretsFile!, 8192);
            var valores = JsonSerializer.Deserialize<Dictionary<string, string>>(conteudo, AcmManualRetryCommandProfile.Json)
                ?? throw new InvalidOperationException("ACM_COMMAND_GUARDIAN_SECRETS_INVALID");
            var lidos = nomes
                .Select(n => (Nome: n, Valor: valores.TryGetValue(n, out var v) ? v : null))
                .Where(p => !string.IsNullOrWhiteSpace(p.Valor))
                .Select(p => (p.Nome, p.Valor!))
                .ToArray();
            if (lidos.Length == 0) throw new InvalidOperationException("ACM_COMMAND_GUARDIAN_SECRETS_EMPTY");
            return lidos;
        }
        return nomes
            .Select(n => (Nome: n, Valor: Environment.GetEnvironmentVariable("Guardian__" + n)))
            .Where(p => p.Valor is not null)
            .Select(p => (p.Nome, p.Valor!))
            .ToArray();
    }

    public static AmbienteBanco ReadEnvironment(AcmManualRetryCommandProfile profile)
    {
        using var file=new MemoryStream(PrivateCommandFile.Read(profile.BaseConfigurationFile,1048576,privateContent:false));
        return AcmEnvironmentBinding.Banco(new ConfigurationBuilder().AddJsonStream(file).Build());
    }

    public static IConfigurationRoot BuildRuntimeConfiguration(AcmManualRetryCommandProfile profile, IConfiguration baseline)
    {
        var ambiente=AcmEnvironmentBinding.Banco(baseline);
        var connection = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5433, Database = ambiente.Banco, SearchPath = "fugates",
            Username = ambiente.PapelAcm, Passfile = profile.Passfile, Enlist = false,
            IncludeErrorDetail = false, ApplicationName = "FugaTES.AcmManualRetry.Command" };
        var result = new ConfigurationBuilder().AddInMemoryCollection(baseline.AsEnumerable()).Build();
        // Explicit per-process overrides. Never inherit a pilot/window or any global worker switch.
        result["ConnectionStrings:FugaTES"] = connection.ConnectionString;
        result["FUGATES_INTEGRATION_WORKER_ENABLED"] = "false";
        result["HostIntegracao:PersistenceMode"] = "PostgreSql";
        result["HostIntegracao:ExternalMode"] = "Guardian";
        result["HostIntegracao:Mode"] = "";
        result["MotorIntegracao:AcmInboundWindow:Enabled"] = "false";
        // O reenvio e ligado a UM contrato pelo piloto. Herdar tambem o recorte continuo daria dois
        // recortes ativos, que MotorIntegracaoOptions.EhValida recusa - e o processo nem subiria.
        result["MotorIntegracao:AcmAutomatico:Enabled"] = "false";
        result["MotorIntegracao:AcmPilotNumeroContrato"] = profile.Contract;
        result["MotorIntegracao:AcmPilotItem"] = profile.Item;
        result["MotorIntegracao:AcmPilotCentro"] = profile.Contract is null ? null : "3011";
        result["AcmR57:Enabled"] = "true";
        result["AcmR57:Normal:Enabled"] = "true";
        return result;
    }
    // Register the approved options BEFORE composition inspects conditional dependencies.
    // Replacing only the options afterward leaves the dynamic resolver absent.
    public static void ConfigureApprovedRuntime(IServiceCollection services,IConfigurationRoot runtime,AcmR57NormalOptions normal)
    {
        runtime["AcmR57:Normal:DynamicEmitterValidationEnabled"]=normal.DynamicEmitterValidationEnabled?"true":"false";
        runtime["AcmR57:Normal:DynamicMaterialValidationEnabled"]=normal.DynamicMaterialValidationEnabled?"true":"false";
        services.RemoveAll<AcmR57NormalOptions>();
        services.AddSingleton(normal);
        HostIntegracaoComposition.ConfigurarServicos(services,runtime);
    }
    public static AcmR57NormalOptions ApproveNormal(AcmManualRetryCommandProfile profile, AcmRetryApproval approval, AcmR57NormalOptions normal)
    {
        profile.Validate([approval]);
        if (normal.ManualRecoveryScope is not null || normal.AcmInboundWindow.Enabled || normal.ApprovedWindowPartnerMappings.Length != 0 || normal.ApprovedWindowMaterialMappings.Length != 0 ||
            normal.GuardianEnvironment != "HML" || !AcmManualRetryPolicy.Evidence(normal.GuardianIdentityEvidence) ||
            !AcmManualRetryPolicy.Evidence(normal.PostgreSqlIdentityEvidence) || !AcmManualRetryPolicy.Evidence(normal.JournalEvidence) ||
            !AcmManualRetryPolicy.Evidence(normal.IdentifierMappingEvidence) ||
            (normal.ApprovedPartnerMapping is { } partner && (partner.NumeroContrato != profile.Contract || partner.Item != profile.Item || partner.Centro != "3011")) ||
            (normal.ApprovedMaterialMapping is { } material && (material.NumeroContrato != profile.Contract || material.Item != profile.Item || material.Centro != "3011")))
            throw new InvalidOperationException("ACM_COMMAND_NORMAL_SCOPE_EVIDENCE_INVALID");
        if(approval.RecoverLatestPending||approval.AllowedStage==AcmRetryStage.SoapOnly)
        {
            if(normal.DynamicEmitterSignatureId<=0)throw new InvalidOperationException("ACM_COMMAND_EMITTER_SCOPE_REQUIRED");
            return normal with{Enabled=true,ExecutionAuthorized=true,AuthorizationReference=approval.AuthorizationReference,
                AcmInboundWindow=new(),AcmAutomatico=new(),DynamicEmitterValidationEnabled=true,
                ManualRecoveryScope=new(){IntegrationId=approval.IntegrationId,Contract=approval.Contract,Item=approval.Item,
                    Version=approval.ExpectedVersion,Fingerprint=approval.Fingerprint,ReceivedUtc=approval.ExpectedReceivedUtc!.Value,
                    SoapOnly=approval.AllowedStage==AcmRetryStage.SoapOnly,
                    AuthorizationReference=approval.AuthorizationReference,NotBeforeUtc=approval.NotBeforeUtc,UntilUtc=approval.UntilUtc}};
        }
        return normal with { Enabled = true, ExecutionAuthorized = true, AuthorizationReference = approval.AuthorizationReference, AcmInboundWindow = new() };
    }
    public static AcmRetryDispatchQueue CreateDispatchQueue(AcmManualRetryCommandProfile profile,IReadOnlyList<AcmRetryApproval> approvals)
    {
        profile.Validate(approvals);
        var queue=new AcmRetryDispatchQueue();
        // Existing Start still proves durable admission, original actor/snapshot, open
        // window, no prior start and no unsafe effect. No new request or backlog polling.
        if(profile.ResumeCommittedCommandId is {} command)queue.Schedule(new(command,approvals.Single()));
        return queue;
    }
    sealed class FixedCommandWorker(string identity) : IIdentidadeWorkerProvider
    {
        public IdentidadeWorkerIntegracao Obter() => new(identity);
    }
    sealed class UnapprovedStore : IAcmManualRetryStore
    {
        static InvalidOperationException Block() => new("ACM_COMMAND_NO_APPROVED_TARGET");
        public Task<AcmRetrySnapshot?> ReadAsync(Guid id, CancellationToken ct) => throw Block();
        public Task<AcmRetryAdmission> AdmitAsync(AcmRetryRequest r, AcmRetryActor a, AcmRetryApproval p, DateTimeOffset n, CancellationToken ct) => throw Block();
        public Task<AcmRetryWork?> StartAsync(Guid id, AcmRetryApproval a, string w, DateTimeOffset n, CancellationToken ct) => throw Block();
        public Task CompleteAsync(AcmRetryWork w, string r, DateTimeOffset n, CancellationToken ct) => throw Block();
        public Task<Guid?> PendingCommandAsync(AcmRetryApproval a, CancellationToken ct) => throw Block();
    }
}




