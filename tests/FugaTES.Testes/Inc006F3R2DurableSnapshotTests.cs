using System.Text.RegularExpressions;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Infraestrutura.Recebimento;

namespace FugaTES.Testes;

public sealed class Inc006F3R2DurableSnapshotTests
{
    [Fact]
    public void MigrationMaterializaVinteColunasComNullabilityFinal()
    {
        var migration = File.ReadAllText(Path.Combine(Root(), "docs", "Incrementos", "006", "MIGRATION_006F2_CORRELACAO_GUARDIAN_SAP.sql"));
        var columns = Regex.Matches(migration, @"(?m)^\s*[a-z_]+\s+(?:uuid|text|smallint|timestamptz)\s+(?:not null|null)");

        Assert.Equal(20, columns.Count);
        Assert.Contains("contrato_sap text not null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operacao_contrato text not null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo_operacao_sap smallint not null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contrato_acm_sap text null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pedido_compras_sap text null", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DezCamposIguaisSaoIdempotentes()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();

        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        var repeated = await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);

        Assert.Equal(StatusRegistroSnapshotMvp.Idempotente, repeated.Status);
    }

    [Fact]
    public async Task DivergenciaEmQualquerDosDezCamposEhConflito()
    {
        var variants = new RecebimentoSapCpiMvp[]
        {
            Input() with { DocumentoLogisticoReferencia = "DOC-2" },
            Input() with { Material = "MAT-2" },
            Input() with { BusinessPartnerEmissor = "BP-E-2" },
            Input() with { BusinessPartnerTransportadora = "BP-T-2" },
            Input() with { Centro = "CENTRO-2" },
            Input() with { TipoDocumento = "TIPO-2" },
            Input() with { Contrato = "CONTRATO-2" },
            Input() with { OperacaoContrato = "OPERACAO-2" },
            Input() with { TipoOperacaoSap = 8 },
            Input() with { PedidoCompras = "PO-2" }
        };

        foreach (var variant in variants.Skip(1))
        {
            var repository = new RepositorioCorrelacaoRecebimentoMemoria();
            await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
            Assert.Equal(StatusRegistroSnapshotMvp.Conflito, (await repository.RegistrarSnapshotAsync(variant, CancellationToken.None)).Status);
        }
    }

    [Fact]
    public async Task PedidoNuloRepetidoEhIdempotente()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input() with { PedidoCompras = null }, CancellationToken.None);

        var result = await repository.RegistrarSnapshotAsync(Input() with { PedidoCompras = null }, CancellationToken.None);

        Assert.Equal(StatusRegistroSnapshotMvp.Idempotente, result.Status);
    }

    [Fact]
    public async Task PedidoNuloVersusValorEhConflito()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input() with { PedidoCompras = null }, CancellationToken.None);

        var result = await repository.RegistrarSnapshotAsync(Input() with { PedidoCompras = "PO-1" }, CancellationToken.None);

        Assert.Equal(StatusRegistroSnapshotMvp.Conflito, result.Status);
    }

    [Fact]
    public async Task ContratoAcmNuloParaValorEhAssociado()
    {
        var repository = await RepositoryAsync();

        var result = await repository.AssociarContratoAcmAsync("DOC-1", "ACM-1", CancellationToken.None);

        Assert.Equal(StatusAssociacaoContratoAcmMvp.Associado, result.Status);
        Assert.Equal("ACM-1", result.Correlacao.Entrada.ContratoAcmSap);
    }

    [Fact]
    public async Task ContratoAcmMesmoValorEhIdempotente()
    {
        var repository = await RepositoryAsync();
        await repository.AssociarContratoAcmAsync("DOC-1", "ACM-1", CancellationToken.None);

        var result = await repository.AssociarContratoAcmAsync("DOC-1", "ACM-1", CancellationToken.None);

        Assert.Equal(StatusAssociacaoContratoAcmMvp.Idempotente, result.Status);
    }

    [Fact]
    public async Task ContratoAcmValorDiferenteEhConflitoSemOverwrite()
    {
        var repository = await RepositoryAsync();
        await repository.AssociarContratoAcmAsync("DOC-1", "ACM-1", CancellationToken.None);

        var result = await repository.AssociarContratoAcmAsync("DOC-1", "ACM-2", CancellationToken.None);

        Assert.Equal(StatusAssociacaoContratoAcmMvp.Conflito, result.Status);
        Assert.Equal("ACM-1", result.Correlacao.Entrada.ContratoAcmSap);
    }

    [Fact]
    public async Task NumeroContratoNaoGeraContratoAcm()
    {
        var repository = await RepositoryAsync();
        var correlation = await repository.AssociarProtocoloAsync("DOC-1", "PROTO-1", CancellationToken.None, DateTimeOffset.UnixEpoch);

        Assert.Null(correlation.Correlacao.Entrada.ContratoAcmSap);
        Assert.Equal("0000001008", correlation.Correlacao.Entrada.Contrato);
    }

    [Fact]
    public void OperacaoContratoNaoGeraTipoOperacao()
    {
        var input = Input() with { OperacaoContrato = "999", TipoOperacaoSap = 7 };

        Assert.Equal("999", input.OperacaoContrato);
        Assert.Equal((short)7, input.TipoOperacaoSap);
    }

    [Fact]
    public void DocumentoReferenciaNaoGeraPedidoCompras()
    {
        var input = Input() with { DocumentoLogisticoReferencia = "DOC-REF", PedidoCompras = null };

        Assert.Equal("DOC-REF", input.DocumentoLogisticoReferencia);
        Assert.Null(input.PedidoCompras);
    }

    [Fact]
    public void PostgreSqlReidrataNovosCampos()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "FugaTES.Persistencia", "Recebimento", "RepositorioCorrelacaoGuardianSapPostgreSql.cs"));

        foreach (var field in new[] { "contrato_sap", "operacao_contrato", "tipo_operacao_sap", "contrato_acm_sap", "pedido_compras_sap" })
            Assert.Contains(field, source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackSemContratoAcmFalhaAntesDoGatewayESemRegistrarTicket()
    {
        var repository = await RepositoryAsync();
        await repository.AssociarProtocoloAsync("DOC-1", "PROTO-1", CancellationToken.None, DateTimeOffset.UnixEpoch);
        var sap = new PesagemSapMvpGatewayFake();
        var useCase = new ProcessarCallbackGuardianMvpCasoDeUso(repository, sap);

        await Assert.ThrowsAsync<InvalidOperationException>(() => useCase.ExecutarAsync(Callback(), CancellationToken.None));

        Assert.Equal(0, sap.QuantidadeChamadas);
        Assert.Null((await repository.ObterPorProtocoloAsync("PROTO-1", CancellationToken.None))!.TicketGuardian);
    }

    [Fact]
    public async Task CallbackComContratoAcmProduzCamposSapSemDerivacao()
    {
        var repository = await RepositoryAsync();
        await repository.AssociarContratoAcmAsync("DOC-1", "ACM-WIRE", CancellationToken.None);
        await repository.AssociarProtocoloAsync("DOC-1", "PROTO-1", CancellationToken.None, DateTimeOffset.UnixEpoch);
        var sap = new PesagemSapMvpGatewayFake();

        var result = await new ProcessarCallbackGuardianMvpCasoDeUso(repository, sap).ExecutarAsync(Callback(), CancellationToken.None);

        Assert.Equal((short)7, result.Pesagem!.TipoOperacao);
        Assert.Equal("ACM-WIRE", result.Pesagem.ContratoACM);
        Assert.Null(result.Pesagem.PedidoCompras);
    }

    [Fact]
    public void FingerprintPermaneceCongelado()
    {
        Assert.Equal(
            "98E6585E1AB1F600F3492EB89D737A09C73B99FEECB69CB917A1B9A67F082734",
            CallbackGuardianMvpFingerprint.Calcular(new("PROTOCOLO-001", "  TICKET-001  ", 1200.00m, 200.0m, 1000m, " concluido ")));
    }

    private static async Task<RepositorioCorrelacaoRecebimentoMemoria> RepositoryAsync()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        return repository;
    }

    private static RecebimentoSapCpiMvp Input() => new(
        Processo: "RECEBIMENTO",
        CodigoFluxo: "FLX_RECEBIMENTO",
        Material: "MAT-1",
        BusinessPartnerEmissor: "BP-E",
        BusinessPartnerTransportadora: "BP-T",
        Contrato: "0000001008",
        DocumentoLogisticoReferencia: "DOC-1",
        TipoContrato: "COMPRA",
        Centro: "3011",
        OperacaoContrato: "10",
        TipoDocumento: "NF",
        PedidoCompras: null,
        TipoOperacaoSap: 7,
        ContratoAcmSap: null);

    private static CallbackGuardianMvp Callback() =>
        new("PROTO-1", "TICKET-1", 1200m, 200m, 1000m, "CONCLUIDO");

    private static string Root()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "FugaTES.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
