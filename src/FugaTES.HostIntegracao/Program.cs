using FugaTES.HostIntegracao;

var builder = Host.CreateApplicationBuilder(args);
if(args.Any(a=>a.StartsWith("--acm-provider-readonly",StringComparison.OrdinalIgnoreCase)))
{
    Environment.ExitCode=await AcmProviderReadOnlyEntrypoint.RunAsync(args,builder);
    return;
}
if(args.Any(a=>a.StartsWith(AcmR104OneShotEntrypoint.Argument,StringComparison.Ordinal)))
{
    Environment.ExitCode=await AcmR104OneShotEntrypoint.RunAsync(args,builder);
    return;
}
try
{
    HostIntegracaoComposition.Configurar(builder);
    var host = builder.Build();
    host.Run();
}
catch(Exception ex) when(OperatingSystem.IsLinux() && builder.Configuration["HostIntegracao:Mode"]=="CarrierSync")
{
    // No exception payload, profile path, connection string or secret reaches journald.
    Console.Error.WriteLine(ex is CarrierServiceStartupException failure ? failure.Code : "CARRIER_LINUX_STARTUP_FAILED");
    Environment.ExitCode=1;
}
