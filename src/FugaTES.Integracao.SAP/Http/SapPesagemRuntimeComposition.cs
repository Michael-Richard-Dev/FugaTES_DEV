using System.Globalization;
using System.Net;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.Configuracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Integracao.SAP.Http;

public static class SapPesagemRuntimeComposition
{
    public static IServiceCollection AddSapPesagemRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var enabledText = configuration["Sap:Enabled"];
        if (enabledText is null or "false" or "False")
        {
            services.AddScoped<IPesagemSapMvpGateway, PesagemSapMvpGatewayDesabilitado>();
            return services;
        }
        if (!bool.TryParse(enabledText, out var enabled) || !enabled)
            throw new InvalidOperationException("Sap:Enabled must be true or false.");
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IAutenticacaoSap)))
            throw new InvalidOperationException("SAP runtime authentication is not configured.");
        if (!Uri.TryCreate(configuration["Sap:BaseUrl"], UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Sap:BaseUrl is invalid.");
        var client = configuration["Sap:SapClient"];
        if (string.IsNullOrWhiteSpace(client))
            throw new InvalidOperationException("Sap:SapClient is absent.");
        if (!TimeSpan.TryParse(configuration["Sap:Timeout"], CultureInfo.InvariantCulture, out var timeout) ||
            timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Sap:Timeout is invalid.");

        services.AddSingleton(new OpcoesSap { BaseUrl = baseUrl, SapClient = client, Timeout = timeout });
        services.AddSingleton<HttpClient>();
        services.AddSingleton<CookieContainer>();
        services.AddScoped<IPesagemSapMvpGateway, PesagemSapHttpGateway>();
        return services;
    }
}

public sealed class PesagemSapMvpGatewayDesabilitado : IPesagemSapMvpGateway
{
    public Task EnviarAsync(PesagemSapMvp pesagem, CancellationToken cancellationToken) =>
        throw new FalhaPesagemSapMvpException("SAP weighing runtime is disabled.",
            ClassificacaoFalhaIntegracao.FalhaDefinitiva);
}
