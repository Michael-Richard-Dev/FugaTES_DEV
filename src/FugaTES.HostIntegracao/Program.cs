using FugaTES.HostIntegracao;

var builder = Host.CreateApplicationBuilder(args);
if(builder.Configuration["HostIntegracao:Mode"]=="MmPurchaseOrders")
{
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(x=>x.SingleLine=true);
    // O ARQUIVO DE RUNTIME DO MM E O DO AMBIENTE DESTE PROCESSO.
    //
    // O caminho estava fixo em /etc/fugates/mm/runtime.json, e isso era uma armadilha silenciosa
    // com dois ambientes: um segundo motor MM leria a configuracao do DEV - Database=fugates_dev,
    // Role=fugates_mm_app, StateDirectory=/var/lib/fugates-mm - por mais que a unidade dele
    // dissesse outra coisa.
    //
    // E dissesse mesmo sem adiantar: este AddJsonFile entra DEPOIS das variaveis de ambiente, e
    // a ultima fonte vence. A unidade do QAS poderia declarar o banco do QAS em Environment= e
    // ainda assim gravar no do DEV, com credencial valida e sem erro nenhum - o mesmo cruzamento
    // que o par (banco, papel) passou o dia fechando.
    //
    // O ambiente vem do par SAP, como em todo o resto. Sem Sap:BaseUrl e Sap:SapClient no
    // ambiente, AcmEnvironmentBinding devolve DEV110: o motor que roda hoje continua lendo
    // exatamente o arquivo que sempre leu.
    builder.Configuration.AddJsonFile(
        AcmEnvironmentBinding.Sap(builder.Configuration).Nome == "DEV110"
            ? "/etc/fugates/mm/runtime.json"
            : "/etc/fugates/qas120/mm-runtime.json",
        optional:true,reloadOnChange:false);
    builder.Services.Configure<HostOptions>(x=>x.ShutdownTimeout=TimeSpan.FromSeconds(90));
}
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
catch(Exception ex) when(OperatingSystem.IsLinux() && builder.Configuration["HostIntegracao:Mode"] is "CarrierSync" or "MmPurchaseOrders")
{
    // No exception payload, profile path, connection string or secret reaches journald.
    Console.Error.WriteLine(ex is CarrierServiceStartupException failure ? failure.Code : "CARRIER_LINUX_STARTUP_FAILED");
    Environment.ExitCode=1;
}
