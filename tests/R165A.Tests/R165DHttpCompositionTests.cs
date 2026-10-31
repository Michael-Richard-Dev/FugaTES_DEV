using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Persistencia.Recebimento;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Text;
using Xunit;

public sealed class R165DHttpCompositionTests
{
    [Fact]
    public async Task Real_http_handler_does_not_write_ack_until_event_store_completes()
    {
        var store = new DelayedStore();
        using var services = new ServiceCollection()
            .AddSingleton<IRegistroEventoGuardianStore>(store)
            .AddScoped<ProcessarEventoGuardianCasoDeUso>().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(CallbackContractTests.Minimal));
        context.Response.Body = new MemoryStream();
        var handling = GuardianCallbackEndpoint.HandleEventoAsync(context);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(handling.IsCompleted);
        Assert.Equal(0, context.Response.Body.Length);
        store.Release.SetResult();
        await handling;
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Equal("OK", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public void PostgreSql_callback_resolves_the_existing_durable_event_store_without_opening_a_connection()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Callback:Enabled"] = "true",
            ["Callback:Route"] = GuardianCallbackEndpoint.Route,
            ["Correlation:Provider"] = "PostgreSql",
            ["ConnectionStrings:FugaTES"] = "Host=127.0.0.1;Port=5433;Database=synthetic;Username=synthetic"
        }).Build();
        GuardianCallbackHttpComposition.ConfigurarServicos(services, configuration);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<RepositorioEventoGuardianPostgreSql>(
            scope.ServiceProvider.GetRequiredService<IRegistroEventoGuardianStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProcessarEventoGuardianCasoDeUso>());
    }

    [Fact]
    public void No_callback_route_is_enabled_by_default()
    {
        var services = new ServiceCollection();
        var routes = InboundHttpHostComposition.Configurar(services,
            new ConfigurationBuilder().Build(), isDevelopment: false);
        Assert.Null(routes.Callback);
        Assert.False(routes.SapProductive);
        Assert.False(routes.SapOffline);
    }

    private sealed class DelayedStore : IRegistroEventoGuardianStore
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<StatusRegistroEventoGuardian> RegistrarAsync(
            EventoGuardianRecebimento evento, string fingerprint, CancellationToken ct)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(ct);
            return StatusRegistroEventoGuardian.Novo;
        }
    }
}
