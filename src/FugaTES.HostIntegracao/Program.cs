using FugaTES.HostIntegracao;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<HospedagemIntegracaoWorker>();

var host = builder.Build();
host.Run();
