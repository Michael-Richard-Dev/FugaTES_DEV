using FugaTES.HostIntegracao;

var builder = Host.CreateApplicationBuilder(args);
HostIntegracaoComposition.Configurar(builder);

var host = builder.Build();
host.Run();
