using System.Net;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.Recebimento;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FugaTES.Testes;

public sealed class Inc006CallbackHmlReadinessTests
{
    [Fact]
    public void Configuracao_explicita_in_memory_registra_repositorio()
    {
        var services = new ServiceCollection();

        var options = GuardianCallbackHttpComposition.ConfigurarServicos(services, Configuration());
        using var provider = services.BuildServiceProvider();

        Assert.True(options.Enabled);
        Assert.Equal(GuardianCallbackEndpoint.Route, options.Route);
        Assert.IsType<RepositorioCorrelacaoRecebimentoMemoria>(provider.GetRequiredService<IRepositorioCorrelacaoRecebimento>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PostgreSql")]
    [InlineData("Unknown")]
    public void Estrategia_ausente_ou_nao_implementada_falha_fechado(string? implementation)
    {
        var configuration = Configuration(implementation);

        Assert.Throws<InvalidOperationException>(() =>
            GuardianCallbackHttpComposition.ConfigurarServicos(new ServiceCollection(), configuration));
    }

    [Theory]
    [InlineData("Callback:Enabled")]
    [InlineData("Callback:Route")]
    public void Configuracao_callback_obrigatoria_ausente_falha_fechado(string key)
    {
        var values = ValidValues();
        values.Remove(key);

        Assert.Throws<InvalidOperationException>(() =>
            GuardianCallbackHttpComposition.ConfigurarServicos(
                new ServiceCollection(),
                new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    [Fact]
    public async Task Health_real_em_loopback_retorna_ok_sem_dependencias_externas()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        var callbackOptions = GuardianCallbackHttpComposition.ConfigurarServicos(builder.Services, Configuration());
        await using var app = builder.Build();
        HealthEndpoint.Map(app);
        GuardianCallbackEndpoint.Map(app, callbackOptions.Route);

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var response = await client.GetAsync(HealthEndpoint.Route);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("OK", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static IConfiguration Configuration(string? implementation = CallbackHttpOptions.InMemoryImplementation)
    {
        var values = ValidValues();
        values["Correlation:Provider"] = implementation;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Dictionary<string, string?> ValidValues() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Callback:Enabled"] = "true",
        ["Callback:Route"] = GuardianCallbackEndpoint.Route,
        ["Correlation:Provider"] = CallbackHttpOptions.InMemoryImplementation
    };
}
