using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using Xunit;

public sealed class PilotRestOutcomeTests
{
    [Theory]
    [InlineData(400, false, StatusFluxoMvp.FalhaNaoReprocessavel)]
    [InlineData(401, false, StatusFluxoMvp.FalhaNaoReprocessavel)]
    [InlineData(422, false, StatusFluxoMvp.FalhaNaoReprocessavel)]
    [InlineData(503, true, StatusFluxoMvp.ResultadoIndeterminado)]
    public async Task Known_REST_status_is_durable_without_losing_confirmed_SOAP_or_retrying(int http, bool unknown, StatusFluxoMvp status)
    {
        var f = new AutomaticFixture();
        var (version, claim) = f.Add("1155");
        f.Rest.Failure = new SafeFailure(http, unknown);
        var result = await f.Pipeline().ExecutarAsync(claim, default);
        Assert.Equal(status, result.Status);
        Assert.Equal($"GUARDIAN_REST_HTTP_{http}", result.ErroCodigo);
        Assert.Equal("1155", f.H4.Items[version.IntegracaoId].CodigoContratoConfirmado);
        Assert.Null(f.H4.Items[version.IntegracaoId].ProtocoloIdGuardian);
        Assert.Equal(result, f.Journal.Results[version.IntegracaoId]);
        Assert.Equal(result, await f.Pipeline().ExecutarAsync(claim, default));
        Assert.Equal(1, f.Soap.Creates);
        Assert.Equal(1, f.Rest.Creates);
    }

    [Fact]
    public async Task Positive_real_protocol_ID_is_not_rejected_because_it_matches_a_historical_fixture()
    {
        var f = new AutomaticFixture();
        var (version, claim) = f.Add("1155");
        var pipeline = new AcmR57Pipeline(f.Versions, new AcmR57NormalPreparation(AutomaticFixture.Options()),
            new AcmR57NormalRuntimeGate(AutomaticFixture.Options()), f.Soap, new ProtocolThree(), f.Journal, f.H4);
        Assert.Equal(StatusFluxoMvp.AguardandoCallback, (await pipeline.ExecutarAsync(claim, default)).Status);
        Assert.Equal(3, f.H4.Items[version.IntegracaoId].ProtocoloIdGuardian);
    }

    private sealed class SafeFailure(int status, bool unknown) : Exception, IAcmGuardianHttpFailure
    {
        public int HttpStatusCode => status;
        public string SafeCode => $"GUARDIAN_REST_HTTP_{status}";
        public string? SafeBusinessMessage => null;
        public bool ResultadoIndeterminado => unknown;
    }
    private sealed class ProtocolThree : IAcmPreCadastroMinimoGatewayR57
    {
        public Task<ProtocoloAcmR57> EnviarMinimoAcmAsync(PreCadastroAcmR57 r, CancellationToken ct)
            => Task.FromResult(new ProtocoloAcmR57(3, r.Dados.CodigoContrato, r.Dados.CodigoItem,
                r.Dados.CodigoEmissor, r.Dados.NumeroReferencia));
    }
}
