using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Servicos.SAP;
using FugaTES.HostIntegracao;
using Microsoft.Extensions.DependencyInjection;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Infraestrutura.Recebimento;

namespace FugaTES.Testes;

public sealed class MvpConfirmedFunctionalRulesTests
{
    [Fact]
    public async Task Response_id_e_numeroTicket_correlacionam_separados_e_ticket_divergente_nao_substitui()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        var response = new ProtocoloGuardianSet26("98765", DateTimeOffset.UtcNow, "12345");
        await repository.RegistrarSnapshotAsync(new("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-TEST", "BP-TEST",
            "TR-TEST", "CON-TEST", "DOC-TEST", "TYPE", "CENTRO-TEST", "10", "NF", "PO-TEST", 10, null),
            CancellationToken.None);
        await repository.AssociarProtocoloAsync("DOC-TEST", response.Valor, CancellationToken.None,
            response.DataCadastro!.Value);
        var callback = new CallbackGuardianMvp(response.Valor, response.NumeroTicket!, 100m, 40m, 60m, "PesagemFinal");

        var first = await repository.RegistrarCallbackAsync(callback, CancellationToken.None);
        var replay = await repository.RegistrarCallbackAsync(callback, CancellationToken.None);
        var conflict = await repository.RegistrarCallbackAsync(callback with { Sequencial = "12346" }, CancellationToken.None);
        var reloaded = await repository.ObterPorProtocoloAsync("98765", CancellationToken.None);

        Assert.Equal(StatusRegistroCallbackMvp.Novo, first.Status);
        Assert.Equal(StatusRegistroCallbackMvp.Idempotente, replay.Status);
        Assert.Equal(StatusRegistroCallbackMvp.Conflito, conflict.Status);
        Assert.Equal("98765", reloaded!.ProtocoloGuardian);
        Assert.Equal("12345", reloaded.TicketGuardian);
        Assert.Equal(first.Correlacao, reloaded);
        Assert.Equal(first.Correlacao, conflict.Correlacao);
    }

    [Fact]
    public void Emissor_SD_confirmado_usa_SoldToParty_na_composition()
    {
        var services = new ServiceCollection();
        services.AddMvpOrchestration();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var prep = scope.ServiceProvider.GetRequiredService<IPreparacaoSaidaSdMvp>();
        Assert.Equal("SOLD-TO", prep.ResolverPartner(new("SO-1", null, "SOLD-TO", null, null, [])));
    }

    [Theory]
    [InlineData("SD_PARTNER_MAPPING")]
    [InlineData("SD_CODIGO_CONTRATO")]
    [InlineData("MM_CODIGO_CONTRATO")]
    [InlineData("GUARDIAN_ID_CALLBACK_EQUIVALENCE")]
    [InlineData("GUARDIAN_TICKET_SEQUENCIAL_EQUIVALENCE")]
    public void Decisao_confirmada_nao_permanece_bloqueada(string decision)
    {
        var services = new ServiceCollection();
        services.AddMvpOrchestration();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDecisoesFluxoMvp>().Exigir(decision);
    }

    [Theory]
    [InlineData(10, TipoOperacaoGuardian.Recebimento)]
    [InlineData(20, TipoOperacaoGuardian.Recebimento)]
    [InlineData(30, TipoOperacaoGuardian.Recebimento)]
    [InlineData(40, TipoOperacaoGuardian.Expedicao)]
    [InlineData(50, TipoOperacaoGuardian.Expedicao)]
    [InlineData(60, TipoOperacaoGuardian.Expedicao)]
    [InlineData(70, TipoOperacaoGuardian.Expedicao)]
    public void Operacao_contrato_mapeia_apenas_dominio_confirmado(int code, TipoOperacaoGuardian expected)
        => Assert.Equal(expected, RegrasFuncionaisMvpConfirmadas.MapearTipoOperacao(code));

    [Fact]
    public void Dominio_SAP_e_Guardian_tem_codigos_exatos()
    {
        Assert.Equal([10, 20, 30, 40, 50, 60, 70], Enum.GetValues<OperacaoContratoSap>().Select(o => (int)o));
        Assert.Equal(10, (int)OperacaoContratoSap.CompraRegular);
        Assert.Equal(20, (int)OperacaoContratoSap.CompraTriangular);
        Assert.Equal(30, (int)OperacaoContratoSap.CompraEntregaFutura);
        Assert.Equal(40, (int)OperacaoContratoSap.VendaRegular);
        Assert.Equal(50, (int)OperacaoContratoSap.VendaContaEOrdem);
        Assert.Equal(60, (int)OperacaoContratoSap.VendaEntregaFutura);
        Assert.Equal(70, (int)OperacaoContratoSap.VendaExportacao);
        Assert.Equal(1, (int)TipoOperacaoGuardian.Recebimento);
        Assert.Equal(2, (int)TipoOperacaoGuardian.Expedicao);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(80)]
    public void Operacao_nao_confirmada_nao_defaulta(int code)
        => Assert.Throws<DecisaoFuncionalPendenteSapException>(() => RegrasFuncionaisMvpConfirmadas.MapearTipoOperacao(code));

    [Fact]
    public void SoldToParty_e_SC_sao_identidades_distintas()
    {
        var order = Order();
        Assert.Equal("SOLD-TO", RegrasFuncionaisMvpConfirmadas.ResolverEmissor(order));
        Assert.Equal("CARRIER-SC", RegrasFuncionaisMvpConfirmadas.ResolverTransportadora(order));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" SOLD-TO")]
    public void Emissor_ausente_nao_deriva_outro_parceiro(string? soldTo)
        => Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            RegrasFuncionaisMvpConfirmadas.ResolverEmissor(Order() with { SoldToParty = soldTo }));

    [Fact]
    public void SC_ausente_ambiguo_ou_case_incorreto_fail_closed()
    {
        foreach (var partners in new IReadOnlyList<SalesOrderPartnerSnapshot>?[] { null, [],
            [new("sc", "CARRIER")], [new("SC", "A"), new("SC", "B")], [new("SC", "")] })
            Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
                RegrasFuncionaisMvpConfirmadas.ResolverTransportadora(Order() with { Partners = partners }));
    }

    [Theory]
    [InlineData("CRM010", false, true)]
    [InlineData("CRM010", true, false)]
    [InlineData("crm010", false, false)]
    [InlineData("FLCU00", false, false)]
    public void Transportadora_exige_CRM010_e_nao_bloqueada(string role, bool blocked, bool accepted)
    {
        var bp = new PartnerSnapshot("CARRIER-SC", null, 2, [], blocked, [role]);
        if (accepted) RegrasFuncionaisMvpConfirmadas.ValidarTransportadora("CARRIER-SC", bp);
        else Assert.Throws<DecisaoFuncionalPendenteSapException>(() => RegrasFuncionaisMvpConfirmadas.ValidarTransportadora("CARRIER-SC", bp));
    }

    [Fact]
    public void Role_ausente_e_BP_divergente_rejeitados()
    {
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            RegrasFuncionaisMvpConfirmadas.ValidarTransportadora("CARRIER-SC", new("CARRIER-SC", null, 2, [], false)));
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            RegrasFuncionaisMvpConfirmadas.ValidarTransportadora("CARRIER-SC", new("OTHER", null, 2, [], false, ["CRM010"])));
    }

    [Fact]
    public void Delivery_seleciona_por_correspondencia_explicita_centro_status_e_ordem()
    {
        var correct = Delivery("DEL-CORRECT", "SP-01", "A");
        var selected = RegrasFuncionaisMvpConfirmadas.SelecionarRemessa(Order(),
            [Delivery("OTHER-SP", "CENTRO-01", "A"), Delivery("STATUS-C", "SP-01", "C"),
             Delivery("OTHER-ORDER", "SP-01", "A", "OTHER-SO"), correct], Mapping());
        Assert.Same(correct, selected);
    }

    [Fact]
    public void Delivery_ambigua_nao_seleciona_primeira()
        => Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            RegrasFuncionaisMvpConfirmadas.SelecionarRemessa(Order(),
                [Delivery("ONE", "SP-01", "A"), Delivery("TWO", "SP-01", "A")], Mapping()));

    [Fact]
    public void Correspondencia_ausente_nao_assume_centro_igual_ShippingPoint()
        => Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            RegrasFuncionaisMvpConfirmadas.SelecionarRemessa(Order(), [Delivery("ONE", "CENTRO-01", "A")], new Dictionary<string, string>()));

    [Fact]
    public void Centros_divergentes_nao_inventam_selecao()
        => Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            RegrasFuncionaisMvpConfirmadas.SelecionarRemessa(Order() with
            { Items = [new("10", null, null, null, "CENTRO-01"), new("20", null, null, null, "CENTRO-02")] }, [], Mapping()));

    [Fact]
    public void Sem_delivery_elegivel_nao_inventa_documento()
        => Assert.Null(RegrasFuncionaisMvpConfirmadas.SelecionarRemessa(Order(), [Delivery("DONE", "SP-01", "C")], Mapping()));

    [Fact]
    public void Sem_ACM_codigoContrato_usa_documento_correto()
    {
        Assert.Equal("SO-1", RegrasFuncionaisMvpConfirmadas.ResolverCodigoContrato(Order()));
        Assert.Equal("PO-1", RegrasFuncionaisMvpConfirmadas.ResolverCodigoContrato(new PurchaseOrderSnapshot("PO-1", null, null, null, [], false)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void ACM_ou_contexto_desconhecido_nao_usa_fallback(bool? hasAcm)
    {
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() => RegrasFuncionaisMvpConfirmadas.ResolverCodigoContrato(Order() with { PossuiContratoAcm = hasAcm }));
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() => RegrasFuncionaisMvpConfirmadas.ResolverCodigoContrato(new PurchaseOrderSnapshot("PO-1", null, null, null, [], hasAcm)));
    }

    [Theory]
    [InlineData("SD_NUMERO_REFERENCIA")]
    [InlineData("MM_NUMERO_REFERENCIA")]
    [InlineData("SD_PATCH_REMESSA")]
    [InlineData("ACM_CONTRATO_ACM_TRANSFORMATION")]
    [InlineData("PRECADASTRO_FINAL")]
    [InlineData("API7")]
    public void Regras_nao_autorizadas_continuam_pendentes(string rule)
        => Assert.Equal(rule, Assert.Throws<DecisaoFuncionalPendenteSapException>(() => new DecisoesMvpConfirmadas().Exigir(rule)).Decisao);

    [Fact]
    public void Mapper_final_e_PATCH_nao_materializam_regras_pendentes()
    {
        var prep = new PreparacaoMvpRegrasConfirmadas();
        var bp = new PartnerSnapshot("SOLD-TO", null, 2, [], false);
        Assert.Equal("SD_NUMERO_REFERENCIA", Assert.Throws<DecisaoFuncionalPendenteSapException>(() => prep.MapearGuardian(Order(), bp, null)).Decisao);
        Assert.Equal("MM_NUMERO_REFERENCIA", Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            prep.MapearGuardian(new PurchaseOrderSnapshot("PO-1", null, null, null, [], false), bp, new("MAT", null, null, null, null))).Decisao);
        Assert.Equal("SD_PATCH_REMESSA", Assert.Throws<DecisaoFuncionalPendenteSapException>(() => prep.MapearPatch(Order(), Delivery("D", "SP-01", "A"))).Decisao);
    }

    private static SalesOrderSnapshot Order() => new("SO-1", null, "SOLD-TO", null, null,
        [new("10", "MAT", 1m, "KG", "CENTRO-01")], [new("AG", "OTHER"), new("SC", "CARRIER-SC")], false);
    private static IReadOnlyDictionary<string, string> Mapping() => new Dictionary<string, string> { ["CENTRO-01"] = "SP-01" };
    private static DeliverySnapshot Delivery(string id, string shippingPoint, string status, string order = "SO-1")
        => new(id, null, shippingPoint, status, null, [new("10", null, null, null, order, "10")]);
}
