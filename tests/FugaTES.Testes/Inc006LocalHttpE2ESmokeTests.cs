using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Integracao.Toledo.Set26;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FugaTES.Testes;

public sealed class Inc006LocalHttpE2ESmokeTests
{
    [Fact]
    public async Task Recebimento_e_callback_compartilham_correlacao_via_tcp_loopback()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        GuardianCallbackHttpComposition.ConfigurarFluxoLocal(
            builder.Services,
            new PreCadastroGuardianSet26GatewayFake("PROTOCOLO-INC006-D1-001"),
            new GuardianSet26Options("/fake/pre-cadastro", "http://127.0.0.1/fake-callback"));

        await using var app = builder.Build();
        GuardianCallbackEndpoint.Map(app);
        using (var scope = app.Services.CreateScope())
        {
            var recebimento = scope.ServiceProvider.GetRequiredService<ProcessarRecebimentoMvpCasoDeUso>();
            await recebimento.ExecutarAsync(Input(), CancellationToken.None);
        }

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var nominal = await client.PostAsJsonAsync(GuardianCallbackEndpoint.Route, Callback());
            using var identical = await client.PostAsJsonAsync(GuardianCallbackEndpoint.Route, Callback());
            using var conflicting = await client.PostAsJsonAsync(
                GuardianCallbackEndpoint.Route,
                Callback() with { PesoBruto = 1300m });

            Assert.Equal(HttpStatusCode.OK, nominal.StatusCode);
            Assert.Equal("PROCESSADO", (string?)(await nominal.Content.ReadFromJsonAsync<JsonObject>())!["status"]);
            var sap = app.Services.GetRequiredService<PesagemSapMvpGatewayFake>();
            var pesagem = Assert.Single(sap.Recebidas);
            Assert.Equal("TICKET-INC006-D1-001", pesagem.IdPesagem);
            Assert.Equal("DOC-INC006-D1-001", pesagem.DocumentoSap);
            Assert.Equal(1200m, pesagem.PesoBruto);
            Assert.Equal(200m, pesagem.PesoTara);
            Assert.Equal(1000m, pesagem.PesoAntesDesconto);

            Assert.Equal(HttpStatusCode.OK, identical.StatusCode);
            Assert.Equal("IDEMPOTENTE", (string?)(await identical.Content.ReadFromJsonAsync<JsonObject>())!["status"]);
            Assert.Single(sap.Recebidas);

            Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
            Assert.Single(sap.Recebidas);

            var repository = app.Services.GetRequiredService<IRepositorioCorrelacaoRecebimento>();
            var correlation = await repository.ObterPorProtocoloAsync("PROTOCOLO-INC006-D1-001", CancellationToken.None);
            Assert.NotNull(correlation);
            Assert.Equal("TICKET-INC006-D1-001", correlation.TicketGuardian);
            Assert.Equal("DOC-INC006-D1-001", correlation.DocumentoSap);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static RecebimentoSapCpiMvp Input() => new(
        "RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-INC006-D1", "BP-EMISSOR", "BP-TRANSPORTADORA",
        "CONTRATO-INC006-D1", "DOC-INC006-D1-001", "COMPRA", "CENTRO-001", "ENTRADA", "NF", "PO-INC006-D1-001", 7, "ACM-INC006-D1-001");

    private static GuardianCallbackHttpDto Callback() => new(
        "PROTOCOLO-INC006-D1-001", "TICKET-INC006-D1-001", 1200m, 200m, 1000m, "CONCLUIDO");
}
