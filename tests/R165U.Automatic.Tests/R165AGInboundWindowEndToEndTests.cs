using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

public sealed class R165AGInboundWindowEndToEndTests
{
    [Theory]
    [InlineData("0000976501")]
    [InlineData("0000976502")]
    public async Task Authenticated_window_admits_unknown_contract_once_then_worker_preserves_backlog(string number)
    {
        var f = new AutomaticFixture();
        var repo = new RepositorioMotorIntegracaoFake();
        var window = R165AGWindowSelectionTests.Window();
        window.Item = null;
        var store = new AtomicCommittedVersions(f.Versions, repo);
        var processingCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = f.Add("1147").Version;
        PilotAutomaticSelectionTests.Seed(repo, older);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(R165AGInboundWindowTests.Config(new()
        {
            ["HostIntegracao:PersistenceMode"] = "Fake",
            ["HostIntegracao:ExternalMode"] = "Fake",
            ["HostIntegracao:PollInterval"] = "00:00:00.020",
            ["MotorIntegracao:AcmInboundWindow:WindowId"] = window.WindowId,
            ["MotorIntegracao:AcmInboundWindow:AuthorizationReference"] = window.AuthorizationReference,
            ["MotorIntegracao:AcmInboundWindow:NotBeforeUtc"] = window.NotBeforeUtc!.Value.ToString("O"),
            ["MotorIntegracao:AcmInboundWindow:UntilUtc"] = window.UntilUtc!.Value.ToString("O")
        }));
        var services = builder.Services;
        services.AddSingleton<IContratoSapToledoVersaoStore>(store);
        services.AddSingleton<ICadastraContratoGuardianGateway>(f.Soap);
        services.AddSingleton<IAcmPreCadastroMinimoGatewayR57>(f.Rest);
        services.AddSingleton<IAcmGuardianVinculoStore>(f.H4);
        services.AddSingleton<IResultadoFluxoMvpStore>(f.Journal);
        services.AddSingleton(R165AGNormalWindowTests.Options(window) with { IdAgenda = 1 });
        services.AddSingleton<ContratoSapToledoCanonicalizer>();
        services.AddScoped<AceitarContratoSapToledoDuravelCasoDeUso>();
        HostIntegracaoComposition.ConfigurarServicos(services, builder.Configuration);
        services.Replace(ServiceDescriptor.Singleton<IMotorIntegracaoRepository>(repo));
        services.Replace(ServiceDescriptor.Scoped<IProcessadorExternoIntegracao>(sp =>
            new JournalClaimAdapter(sp.GetRequiredService<ProcessadorFluxosMvp>(), f.Journal, processingCompleted)));
        f.Soap.BeforeCreate = () =>
        {
            Assert.True(store.Committed);
            Assert.Equal("R165U_SOAP_ARMED", f.Journal.Results[store.AdmittedId].Etapa);
        };
        f.Rest.BeforeCreate = () =>
        {
            Assert.Equal(1, f.Soap.Creates);
            Assert.Equal("R165U_REST_ARMED", f.Journal.Results[store.AdmittedId].Etapa);
        };
        await using var app = builder.Build();
        SapContractInboundEndpoint.MapDurable(app);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = System.Net.Http.Headers.AuthenticationHeaderValue.Parse(R165AGInboundAuthScopeTests.Basic);
            http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            // The loopback HTTP server simulates only the approved proxy handoff. It proves no edge TLS or SAP/CPI delivery.
            var sending = http.PostAsJsonAsync(SapContractInboundEndpoint.Route, Payload(number));
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var concurrentRedelivery = http.PostAsJsonAsync(SapContractInboundEndpoint.Route, Payload(number));
            Assert.False(store.Committed);
            Assert.Equal(0, f.Soap.Creates);
            Assert.Equal(0, f.Rest.Creates);
            Assert.NotSame(sending, await Task.WhenAny(sending, Task.Delay(100)));
            Assert.NotSame(concurrentRedelivery, await Task.WhenAny(concurrentRedelivery, Task.Delay(100)));
            store.AllowCommit.TrySetResult();
            using var first = await sending;
            using var concurrent = await concurrentRedelivery;
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, concurrent.StatusCode);
            using var firstAck = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            using var replayAck = JsonDocument.Parse(await concurrent.Content.ReadAsStringAsync());
            Assert.Equal(1, firstAck.RootElement.GetProperty("versao").GetInt32());
            Assert.Equal(1, replayAck.RootElement.GetProperty("versao").GetInt32());
            Assert.NotEqual(firstAck.RootElement.GetProperty("reentrega").GetBoolean(),
                replayAck.RootElement.GetProperty("reentrega").GetBoolean());
            Assert.Equal(firstAck.RootElement.GetProperty("correlationId").GetString(),
                replayAck.RootElement.GetProperty("correlationId").GetString());
            Assert.Equal(Assert.Single(store.Rows).CorrelationId,
                firstAck.RootElement.GetProperty("correlationId").GetString());
            Assert.StartsWith(window.CorrelationPrefix, firstAck.RootElement.GetProperty("correlationId").GetString());
            // Await the complete dispatcher invocation; the protocol link is saved before the final journal stage.
            // This also avoids enumerating the test dictionaries while the pipeline still writes them.
            await processingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));
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
            using var replay = await http.PostAsJsonAsync(SapContractInboundEndpoint.Route, Payload(number));
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var secondContract = await http.PostAsJsonAsync(SapContractInboundEndpoint.Route, Payload("0000976503"));
            Assert.Equal(HttpStatusCode.Conflict, secondContract.StatusCode);
            Assert.DoesNotContain(nameof(AcmInboundScopeConflictException), await secondContract.Content.ReadAsStringAsync());
            Assert.Single(store.Rows);
            Assert.Equal(1, f.Soap.Creates);
            Assert.Equal(1, f.Rest.Creates);
        }
        finally { store.AllowCommit.TrySetResult(); await app.StopAsync(); }
    }

    private static object Payload(string number) => new
    {
        Contrato = new[] { new { NumeroContrato = number, TipoContrato = "AC3P",
            DescricaoTipoContrato = "SYNTHETIC WINDOW INTEGRATED TEST", Centro = "3011", TipoPesagem = "E",
            OperacaoContrato = "10", StatusContrato = "T", IdParceiro = "0020000051",
            Material = "7000000", QuantidadeContrato = 10000m } }
    };

    private sealed class JournalClaimAdapter(ProcessadorFluxosMvp dispatcher, AutomaticFixture.Ledger ledger,
        TaskCompletionSource processingCompleted)
        : IProcessadorExternoIntegracao
    {
        public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim, CancellationToken ct)
        {
            ledger.Claims[claim.IntegracaoId] = claim;
            try
            {
                var result = await dispatcher.ProcessarAsync(claim, ct);
                processingCompleted.TrySetResult();
                return result;
            }
            catch (Exception error)
            {
                processingCompleted.TrySetException(error);
                throw;
            }
        }
    }

    private sealed class AtomicCommittedVersions(AutomaticFixture.TestVersions versions, RepositorioMotorIntegracaoFake repo)
        : IContratoSapToledoAdmissionStore
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<AcmInboundAdmittedRow> Rows = [];
        public bool Committed;
        public Guid AdmittedId;
        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot snapshot, string fp,
            string payload, string correlation, CancellationToken ct) => throw new InvalidOperationException("TYPED_ADMISSION_REQUIRED");
        public async Task<VersaoContratoSapToledo> RegistrarPrimeiroOuObterReentregaAsync(ContratoSapToledoSnapshot snapshot,
            string fp, string payload, string correlation, AcmInboundAdmission admission, CancellationToken ct)
        {
            Entered.TrySetResult();
            await AllowCommit.Task.WaitAsync(ct);
            // A process-local lock models serialization only; this does not certify PostgreSQL ACID.
            lock (versions.Items)
            {
                var now = DateTimeOffset.UtcNow;
                var existing = AcmInboundAdmissionGuard.Resolver(admission, snapshot, fp, correlation, now, Rows);
                if (existing is not null) return existing;
                var version = new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada,
                    snapshot.NumeroContrato!, 1, fp, Guid.NewGuid(), snapshot) { AdmissionCorrelationId = correlation };
                versions.Items[version.IntegracaoId] = version;
                Rows.Add(new(version, correlation, now, "SAP", "TOLEDO", "SAP_CONTRATO_TOLEDO", snapshot.NumeroContrato!, fp));
                AdmittedId = version.IntegracaoId;
                Committed = true;
                repo.Semear(new(version.IntegracaoId, "SAP", "TOLEDO", "SAP_CONTRATO_TOLEDO", version.NumeroContrato,
                    fp, correlation, EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico, now,
                    0, null, null, FaseEnvioIntegracao.NaoIniciado, null));
                return version;
            }
        }
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id, CancellationToken ct)
        {
            lock (versions.Items) return Task.FromResult(versions.Items.GetValueOrDefault(id));
        }
        public Task<bool> TemPredecessorNaoTerminalAsync(string number, long version, CancellationToken ct) => Task.FromResult(false);
    }
}
