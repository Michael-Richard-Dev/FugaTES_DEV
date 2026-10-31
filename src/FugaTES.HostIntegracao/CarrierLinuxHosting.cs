namespace FugaTES.HostIntegracao;
public static class CarrierLinuxHosting
{
    public static void Configure(IServiceCollection services,IConfiguration config,bool enabled)
    {
        if(IsTrue(config["FUGATES_INTEGRATION_WORKER_ENABLED"])||IsTrue(config["INC005_WORKER_ENABLED"]))throw new CarrierServiceStartupException("CARRIER_INC005_FORBIDDEN");
        if(config["Carrier:Environment"]!="HML")throw new CarrierServiceStartupException("CARRIER_LINUX_ENVIRONMENT_INVALID");
        if(bool.TryParse(config["CARRIER_WORKER_ENABLED"],out var alias)&&alias!=enabled)throw new CarrierServiceStartupException("CARRIER_LINUX_FLAG_MISMATCH");
        services.AddSystemd();services.AddLogging();
        services.Configure<HostOptions>(options=>options.ShutdownTimeout=TimeSpan.FromSeconds(35));
        services.AddSingleton(_=>CarrierLinuxRuntimeBootstrap.StartAsync(config["Carrier:LinuxProfile"]??throw new CarrierServiceStartupException("CARRIER_LINUX_PROFILE_REQUIRED"),enabled,CancellationToken.None).GetAwaiter().GetResult());
        services.AddHostedService<CarrierLinuxReadiness>();
    }
    private static bool IsTrue(string? value)=>bool.TryParse(value,out var flag)&&flag;
}
// Completing StartAsync gates systemd READY=1 on authenticated, read-only bootstrap.
public sealed class CarrierLinuxReadiness(CarrierLinuxRuntimeBootstrap runtime,IConfiguration config,ILogger<CarrierLinuxReadiness> logger):IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();_ = runtime.Source;
        logger.LogInformation("{CarrierDiagnostic}",config["FUGATES_CARRIER_SYNC_ENABLED"]=="true"?"CARRIER_LINUX_READY":"CARRIER_LINUX_READY_WORKER_DISABLED");return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct)=>Task.CompletedTask;
}