using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.HostIntegracao;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FugaTES.Testes;

public sealed class AcmProximityTemporaryR6Tests
{
    internal static ContratoSapToledoSnapshot Snapshot(string number) => AcmGuardianReferenceMapperTests.Snapshot() with
    { NumeroContrato = number, QuantidadeContrato = 50m, DescricaoTipoContrato = "Compra", IdParceiro = "BP", Material = "MAT", DiasToler = 999 };
    internal static ServiceProvider Provider(string environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new EnvironmentFake { EnvironmentName = environment });
        services.AddMvpOrchestration();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("HML", "0000001031")]
    [InlineData("HML", "0000098765")]
    [InlineData("MVP", "0000098765")]
    public void Hml_mvp_context_maps_zero_for_any_contract(string environment, string number)
    {
        using var provider = Provider(environment);
        var mapped = provider.GetRequiredService<IMapeadorContratoAcmGuardian>().Mapear(Snapshot(number));
        Assert.Equal(0m, mapped.ValorProximidadeFimContrato);
        Assert.Equal(number, mapped.Codigo);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("UNKNOWN")]
    public void No_temporary_assumption_outside_hml_mvp(string environment)
    {
        using var provider = Provider(environment);
        var error = Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            provider.GetRequiredService<IMapeadorContratoAcmGuardian>().Mapear(Snapshot("0000098765")));
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", error.Decisao);
    }

    [Theory]
    [InlineData("3008")]
    [InlineData("3012")]
    public void Other_centers_remain_blocked(string center)
    {
        using var provider = Provider("HML");
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            provider.GetRequiredService<IMapeadorContratoAcmGuardian>().Mapear(Snapshot("0000098765") with { Centro = center }));
    }

    [Theory]
    [InlineData("ACM_SAIDA", "3011")]
    [InlineData("ACM_ENTRADA", "3008")]
    [InlineData("ACM_ENTRADA", "3012")]
    [InlineData("UNKNOWN", "3011")]
    [InlineData("", "3011")]
    public void Policy_rejects_unapproved_flow_or_center(string flow, string center)
    {
        var error = Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            new RegraValorProximidadeFimAcm(null, "HML").Resolver(flow, center));
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", error.Decisao);
    }

    [Fact]
    public void Explicit_rule_can_replace_temporary_assumption()
        => Assert.Equal(2.5m, new RegraValorProximidadeFimAcm(2.5m, "HML").Resolver("ACM_ENTRADA", "3011"));

    [Fact]
    public void H4_version_does_not_select_temporary_rule()
    {
        using var provider = Provider("HML");
        var snapshot = Snapshot("0000098765");
        var version = new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.JaExistente,
            snapshot.NumeroContrato, 1, new string('A', 64), Guid.NewGuid(), snapshot);
        var mapper = provider.GetRequiredService<IMapeadorContratoAcmGuardian>();
        Assert.Equal(mapper.Mapear(version), mapper.Mapear(version with { Versao = 7, IntegracaoId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Temporary_zero_serializes_as_decimal_zero_offline()
    {
        using var provider = Provider("HML");
        var mapped = provider.GetRequiredService<IMapeadorContratoAcmGuardian>().Mapear(Snapshot("0000098765"));
        var handler = new CaptureHandler();
        var client = new FugaTES.Integracao.Guardian.GuardianContratoSoapClient(new HttpClient(handler),
            new FugaTES.Integracao.Guardian.GuardianHttpOptions
            { ContratoSoapEndpoint = new Uri("https://guardian.invalid"), Timeout = TimeSpan.FromSeconds(2) });
        await client.CadastrarAsync(mapped, default);
        System.Xml.Linq.XNamespace ns = "http://toledobrasil.com.br/WS_Guardian";
        Assert.Equal("0", System.Xml.Linq.XDocument.Parse(handler.Body!).Descendants(ns + "ValorProximidadeFimContrato").Single().Value);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body><CadastraContratoResponse xmlns='http://toledobrasil.com.br/WS_Guardian'><Erro>0</Erro></CadastraContratoResponse></s:Body></s:Envelope>") };
        }
    }

    private sealed class EnvironmentFake : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "HML";
        public string ApplicationName { get; set; } = "OfflineTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}