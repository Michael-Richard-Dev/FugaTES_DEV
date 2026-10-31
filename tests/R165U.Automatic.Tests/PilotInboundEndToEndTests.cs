using System.Net;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Callback.Http;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class PilotInboundEndToEndTests
{
    [Theory]
    [InlineData("0000999001")]
    [InlineData("0000999002")]
    public async Task HTTP_commit_then_existing_worker_creates_and_records_simulated_Guardian_results(string number)
    {
        var f = new AutomaticFixture();
        var repo = new RepositorioMotorIntegracaoFake();
        var store = new CommittedVersions(f.Versions, repo);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(R165AGInboundAuthScopeTests.Config(new()
        {
            ["MotorIntegracao:AcmPilotNumeroContrato"] = number,
            ["HostIntegracao:PersistenceMode"] = "Fake",
            ["HostIntegracao:ExternalMode"] = "Fake",
            ["HostIntegracao:PollInterval"] = "00:00:00.020"
        }));
        var services = builder.Services;
        services.AddSingleton<IContratoSapToledoVersaoStore>(store);
        services.AddSingleton<ICadastraContratoGuardianGateway>(f.Soap);
        services.AddSingleton<IAcmPreCadastroMinimoGatewayR57>(f.Rest);
        services.AddSingleton<IAcmGuardianVinculoStore>(f.H4);
        services.AddSingleton<IResultadoFluxoMvpStore>(f.Journal);
        services.AddSingleton(AutomaticFixture.Options() with { IdAgenda = 1 });
        services.AddSingleton<ContratoSapToledoCanonicalizer>();
        services.AddScoped<AceitarContratoSapToledoDuravelCasoDeUso>();
        HostIntegracaoComposition.ConfigurarServicos(services, builder.Configuration);
        services.Replace(ServiceDescriptor.Singleton<IMotorIntegracaoRepository>(repo));
        services.Replace(ServiceDescriptor.Scoped<IProcessadorExternoIntegracao>(sp =>
            new JournalClaimAdapter(sp.GetRequiredService<ProcessadorFluxosMvp>(), f.Journal)));
        var older = f.Add("1147").Version;
        PilotAutomaticSelectionTests.Seed(repo, older);
        f.Soap.BeforeCreate = () => Assert.True(store.Committed);
        f.Rest.BeforeCreate = () => Assert.Contains(f.Journal.History, r => r.Etapa == "R165U_REST_ARMED");
        await using var app = builder.Build();
        SapContractInboundEndpoint.MapDurable(app);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(R165AGInboundAuthScopeTests.Basic);
            // Explicitly synthetic: no SAP/CPI delivery and no real Guardian transport are composed.
            var payload = new
            {
                Contrato = new[] { new { NumeroContrato = number, TipoContrato = "AC3P",
                    DescricaoTipoContrato = "SYNTHETIC INTEGRATED TEST", Centro = "3011", TipoPesagem = "E",
                    OperacaoContrato = "10", StatusContrato = "T", IdParceiro = "0020000051",
                    Material = "000000000007000000", QuantidadeContrato = 10000m } }
            };
            var sending = http.PostAsJsonAsync(SapContractInboundEndpoint.Route, payload);
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var concurrentRedelivery = http.PostAsJsonAsync(SapContractInboundEndpoint.Route, payload);
            Assert.False(store.Committed);
            Assert.Equal(0, f.Soap.Creates);
            Assert.Equal(0, f.Rest.Creates);
            Assert.NotSame(sending, await Task.WhenAny(sending, Task.Delay(100)));
            Assert.NotSame(concurrentRedelivery, await Task.WhenAny(concurrentRedelivery, Task.Delay(100)));
            store.AllowCommit.TrySetResult();
            using var response = await sending;
            using var concurrentResponse = await concurrentRedelivery;
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(HttpStatusCode.Created, concurrentResponse.StatusCode);
            await WaitUntil(() => f.H4.Items.Values.Any(x => x.ProtocoloIdGuardian is > 0));
            var link = Assert.Single(f.H4.Items.Values);
            Assert.Equal(number, link.NumeroContratoSap);
            Assert.Equal(number, link.CodigoContratoConfirmado);
            Assert.Equal("OC" + number.TrimStart('0'), link.NumeroReferenciaSolicitado);
            Assert.Equal(801, link.ProtocoloIdGuardian);
            Assert.Equal(1, f.Soap.Creates);
            Assert.Equal(1, f.Rest.Creates);
            Assert.Single(repo.Tentativas);
            Assert.Equal("R165U_CONFIRMED", f.Journal.Results[link.IntegracaoIdOrigem].Etapa);
            Assert.Equal(EstadoIntegracao.Pendente, (await repo.ObterPorIdAsync(older.IntegracaoId, default))!.Estado);
            using var replay = await http.PostAsJsonAsync(SapContractInboundEndpoint.Route, new
            {
                Contrato = new[] { store.Snapshot }
            });
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal(1, f.Soap.Creates);
            Assert.Equal(1, f.Rest.Creates);
        }
        finally { store.AllowCommit.TrySetResult(); await app.StopAsync(); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class JournalClaimAdapter(ProcessadorFluxosMvp dispatcher, AutomaticFixture.Ledger ledger)
        : IProcessadorExternoIntegracao
    {
        public Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
        {
            ledger.Claims[claim.IntegracaoId] = claim;
            return dispatcher.ProcessarAsync(claim, ct);
        }
    }

    private sealed class CommittedVersions(AutomaticFixture.TestVersions versions, RepositorioMotorIntegracaoFake repo)
        : IContratoSapToledoVersaoStore
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Committed;
        public ContratoSapToledoSnapshot? Snapshot;
        public async Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot s, string fp,
            string payload, string correlation, CancellationToken ct)
        {
            Entered.TrySetResult();
            await AllowCommit.Task.WaitAsync(ct);
            // Simulates the existing PostgreSQL identity/version transaction, not a new production store.
            lock (versions.Items)
            {
                var previous = versions.Items.Values.FirstOrDefault(v => v.FingerprintSha256 == fp);
                if (previous is not null) return previous with { Resultado = ResultadoRegistroVersaoContrato.JaExistente };
                var version = new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada, s.NumeroContrato!, 1, fp, Guid.NewGuid(), s);
                Snapshot = s;
                versions.Items[version.IntegracaoId] = version;
                Committed = true;
                PilotAutomaticSelectionTests.Seed(repo, version);
                return version;
            }
        }
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken ct)
            => versions.ObterPorIntegracaoIdAsync(id, ct);
        public Task<bool> TemPredecessorNaoTerminalAsync(string n, long v, CancellationToken ct) => Task.FromResult(false);
    }
}
