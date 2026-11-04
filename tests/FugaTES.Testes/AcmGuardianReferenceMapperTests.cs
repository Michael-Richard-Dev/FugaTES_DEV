using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.HostIntegracao;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class AcmGuardianReferenceMapperTests
{
    [Fact]
    public void ZeroAprovadoSomenteParaIdentidadeH4Confirmada()
    {
        var versao = VersaoAprovada();
        Assert.Equal(0m, MapeadorContratoAcmGuardian.ResolverValorProximidadeFim(versao, 0m));
        Assert.Equal("0000001031", versao.NumeroContrato);
        Assert.Equal(2, versao.Versao);
    }

    [Theory]
    [InlineData("0000001031", 1)]
    [InlineData("0000001031", 3)]
    [InlineData("1031", 2)]
    [InlineData("0000001032", 2)]
    public void ContratoOuVersaoSemAprovacaoNaoRecebeZero(string numero, long version)
    {
        var versao = VersaoAprovada() with { NumeroContrato = numero, Versao = version,
            Snapshot = Snapshot() with { NumeroContrato = numero } };
        var error = Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.ResolverValorProximidadeFim(versao));
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", error.Decisao);
    }

    [Theory]
    [InlineData("SNAPSHOT")]
    [InlineData("ID")]
    [InlineData("CENTRO")]
    [InlineData("STATUS")]
    [InlineData("OPERACAO")]
    public void IdentidadeOuCenarioInconsistenteNaoRecebeZero(string defect)
    {
        var versao = VersaoAprovada();
        versao = defect switch {
            "SNAPSHOT" => versao with { Snapshot = versao.Snapshot with { NumeroContrato = "1031" } },
            "ID" => versao with { IntegracaoId = Guid.Empty },
            "CENTRO" => versao with { Snapshot = versao.Snapshot with { Centro = "3020" } },
            "STATUS" => versao with { Snapshot = versao.Snapshot with { StatusContrato = "X" } },
            _ => versao with { Snapshot = versao.Snapshot with { OperacaoContrato = "20" } }
        };
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.ResolverValorProximidadeFim(versao));
    }

    internal static VersaoContratoSapToledo VersaoAprovada() => new(ResultadoRegistroVersaoContrato.JaExistente,
        "0000001031", 2, new string('A', 64), Guid.NewGuid(), Snapshot());

    [Fact]
    public void MapperVersionadoPreservaContextoSemInventarRestanteDoSoap()
    {
        IMapeadorContratoAcmGuardian mapper = new MapeadorContratoAcmGuardian();
        var error = Assert.Throws<DecisaoFuncionalPendenteSapException>(() => mapper.Mapear(VersaoAprovada()));
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", error.Decisao);
        var semVersao = Assert.Throws<DecisaoFuncionalPendenteSapException>(() => mapper.Mapear(Snapshot()));
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", semVersao.Decisao);
    }

    [Theory]
    [InlineData("0000001031", "OC1031")]
    [InlineData("1031", "OC1031")]
    [InlineData("000012345678", "OC12345678")]
    public void ReferenciaPreservaIdentidadeSapSemPaddingReverso(string sap, string reference)
    {
        var snapshot = Snapshot() with { NumeroContrato = sap };
        var mapped = MapeadorContratoAcmGuardian.MapearCamposConfirmados(snapshot);
        Assert.Equal(sap, mapped.NumeroContratoSap);
        Assert.Equal(reference, mapped.NumeroReferencia);
        Assert.Equal(sap, snapshot.NumeroContrato);
        Assert.Equal("308102", mapped.Estado);
        Assert.Equal("309837", mapped.TipoLancamento);
        Assert.Equal("FLX_RECEBIMENTO", mapped.CodigoFluxo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0000000000")]
    [InlineData("123456789")]
    [InlineData("10A31")]
    [InlineData("+1031")]
    [InlineData(" 1031")]
    [InlineData("1031 ")]
    public void ReferenciaInvalidaNaoPodeSerEnviada(string? sap)
        => Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot() with { NumeroContrato = sap! }));

    [Theory]
    [InlineData("3020", "AC3P", "T", "10")]
    [InlineData("3011", "OUTRO", "T", "10")]
    [InlineData("3011", "AC3P", "t", "10")]
    [InlineData("3011", "AC3P", "", "10")]
    // R170: a operacao do SAP nao bloqueia mais aqui - ela varia para o mesmo sentido de negocio
    // (10 e 30 sao ambos compra) e nao diz nada sobre credito ou debito. O par fechado continua
    // exigido no escopo (janela R57, AcmAutomatico e predicado do motor), nao neste mapeador.
    public void CombinacaoNaoAprovadaPermaneceBloqueada(string centro, string tipo, string status, string operation)
        => Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot() with {
                Centro = centro, TipoContrato = tipo, StatusContrato = status, OperacaoContrato = operation }));

    [Fact]
    public async Task ReferenciaLivreConsultadaSemPersistirOuEnviar()
    {
        var repository = new Correlacoes();
        var campos = new CamposAcmGuardianConfirmados("0000001031", "OC1031", "308102", "309837", "FLX_RECEBIMENTO");
        await MapeadorContratoAcmGuardian.ExigirReferenciaDisponivelAsync(campos, repository, default);
        Assert.Equal("OC1031", repository.Consultada);
    }

    [Theory]
    [InlineData("1031")]
    [InlineData("0000001031")]
    public async Task ReferenciaExistenteBloqueiaInclusiveVersaoNaoComprovada(string existingSap)
    {
        var repository = new Correlacoes {
            Atual = new("OC1031", null, new("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT", "BP-E", "BP-T",
                existingSap, "OC1031", "AC3P", "3011", "10", "TIPO", null, 10, existingSap)) };
        var campos = new CamposAcmGuardianConfirmados("0000001031", "OC1031", "308102", "309837", "FLX_RECEBIMENTO");
        await Assert.ThrowsAsync<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.ExigirReferenciaDisponivelAsync(campos, repository, default));
        Assert.Equal(existingSap, repository.Atual.Entrada.Contrato);
        Assert.Null(repository.Atual.ProtocoloGuardian);
    }

    [Fact]
    public void MapperCompostoAvancaCodigosAprovadosSemInventarValoresSoap()
    {
        var services = new ServiceCollection();
        services.AddMvpOrchestration();
        using var provider = services.BuildServiceProvider();
        var mapper = provider.GetRequiredService<IMapeadorContratoAcmGuardian>();
        var error = Assert.Throws<DecisaoFuncionalPendenteSapException>(() => mapper.Mapear(Snapshot()));
        Assert.Equal("ACM_VALOR_PROXIMIDADE_FIM", error.Decisao);
    }

    internal static ContratoSapToledoSnapshot Snapshot() => new(null, "0000001031", "AC3P", null,
        null, null, null, null, null, "T", null, null, null, "000000000007000000", null,
        null, null, null, null, null, "3011", "10", []);

    private sealed class Correlacoes : IRepositorioCorrelacaoRecebimento
    {
        public CorrelacaoRecebimentoMvp? Atual;
        public string? Consultada;
        public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string document, CancellationToken ct)
        { Consultada = document; return Task.FromResult(Atual); }
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp entrada, CancellationToken ct) => throw new InvalidOperationException("No writes.");
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string document, string contract, CancellationToken ct) => throw new InvalidOperationException("No writes.");
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string document, string protocol, CancellationToken ct, DateTimeOffset created) => throw new InvalidOperationException("No writes.");
        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocol, CancellationToken ct) => throw new InvalidOperationException("No callback.");
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken ct) => throw new InvalidOperationException("No writes.");
    }
}
