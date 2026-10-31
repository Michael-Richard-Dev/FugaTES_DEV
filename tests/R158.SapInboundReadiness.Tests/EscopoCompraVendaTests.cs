using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;

namespace R158.SapInboundReadiness.Tests;

// R170: o cadastro automatico aceita compra e venda. A regra e o PAR tipo de contrato + tipo de
// pesagem. A operacao do SAP foi deliberadamente retirada da decisao: ela varia para o mesmo
// sentido de negocio (10 e 30 sao ambos compra) e travava contrato legitimo sem acrescentar
// informacao sobre credito ou debito. Estes testes fixam o par aceito e, principalmente, que
// nenhuma mistura passa.
public sealed class EscopoCompraVendaTests
{
    [Theory]
    [InlineData("AC3P", "E")]   // compra: entrada
    [InlineData("AC3S", "S")]   // venda: saida
    public void As_duas_combinacoes_de_negocio_sao_aceitas(string tipo, string pesagem)
        => Assert.True(AcmInboundWindowOptions.EscopoContratoAprovado(tipo, pesagem));

    // Misturar o par e o erro mais perigoso: cadastraria venda com semantica de compra.
    [Theory]
    [InlineData("AC3P", "S")]
    [InlineData("AC3S", "E")]
    [InlineData("AC3X", "E")]
    [InlineData("AC3P", "")]
    [InlineData(null, null)]
    public void Qualquer_mistura_ou_valor_desconhecido_e_recusado(string? tipo, string? pesagem)
        => Assert.False(AcmInboundWindowOptions.EscopoContratoAprovado(tipo, pesagem));

    // A operacao do SAP nao participa da decisao. Este teste existe para que uma futura tentativa
    // de voltar a exigir um valor fixo quebre aqui, e nao em producao com contrato travado.
    [Theory]
    [InlineData("10")]
    [InlineData("30")]
    [InlineData("99")]
    [InlineData(null)]
    public void A_operacao_do_sap_nao_altera_a_decisao(string? operacao)
    {
        Assert.True(AcmInboundWindowOptions.EscopoContratoAprovado("AC3P", "E"));
        var campos = MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot("AC3P", "E", operacao));
        Assert.Equal("309837", campos.TipoLancamento);
    }

    // Valores sinteticos, no formato em que o contrato fica GRAVADO no banco: material preenchido
    // com zeros a esquerda e sem Item, como o transporte SAP -> FugaTES entrega.
    private static ContratoSapToledoSnapshot Snapshot(string? tipo, string? pesagem, string? operacao)
        => new(SistemaBalanca: "Toledo", NumeroContrato: "0000002001", TipoContrato: tipo,
            DescricaoTipoContrato: "ACM contrato", DataEmissao: "2026-10-29", DataEntregaInicio: "2026-10-29",
            DataEntregaFim: "2026-12-31", DiasToler: 0, DataLimiteEntrega: null, StatusContrato: "T",
            QuantidadeContrato: 100000.000m, QuantidadeSaldoDevolucao: 0m, IdParceiro: "0020000051",
            Material: "000000000007000002", TipoPesagem: pesagem, TabelaQualidadeID: "0000000078",
            TabelaQualidadeVersao: "001", TabelaQualidadeVersaoValidade: "001", Observacao: "",
            ValorUnitario: 1.00m, Centro: "3011", OperacaoContrato: operacao, CentroOpcional: []);

    // O sentido do lancamento acompanha a pesagem: entrada credita saldo, saida debita.
    [Theory]
    [InlineData("AC3P", "E", "309837")]
    [InlineData("AC3S", "S", "309838")]
    public void O_tipo_de_lancamento_segue_o_sentido_do_par(string tipo, string pesagem, string esperado)
    {
        var campos = MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot(tipo, pesagem, "10"));
        Assert.Equal(esperado, campos.TipoLancamento);
        Assert.Equal("308102", campos.Estado);   // status T -> ativo, para ambos
    }

    // Tipo de contrato desconhecido nao tem sentido de lancamento: o mapeador recusa.
    [Fact]
    public void O_mapeador_recusa_tipo_de_contrato_desconhecido()
        => Assert.Throws<DecisaoFuncionalPendenteSapException>(
            () => MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot("AC3X", "E", "10")));

    // Fronteira deliberada: o mapeador NAO decide pela pesagem. Endurecer isto quebraria chamadores
    // que nunca preencheram o campo. Quem exige o par fechado e o escopo - e e la que a mistura e
    // recusada, como as duas asercoes abaixo fixam.
    [Theory]
    [InlineData("AC3S", "E", "309838")]   // venda com pesagem de entrada
    [InlineData("AC3P", "S", "309837")]   // compra com pesagem de saida
    public void O_mapeador_nao_decide_pela_pesagem_mas_o_escopo_recusa(string tipo, string pesagem, string lancamento)
    {
        var campos = MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot(tipo, pesagem, "10"));
        Assert.Equal(lancamento, campos.TipoLancamento);
        Assert.False(AcmInboundWindowOptions.EscopoContratoAprovado(tipo, pesagem));
        Assert.False(Janela().SnapshotPermitido(Snapshot(tipo, pesagem, "10")));
    }

    // O centro continua restrito, independentemente do par de negocio.
    [Fact]
    public void Centro_fora_do_aprovado_continua_recusado()
    {
        var fora = Snapshot("AC3S", "S", "40") with { Centro = "3020" };
        Assert.Throws<DecisaoFuncionalPendenteSapException>(
            () => MapeadorContratoAcmGuardian.MapearCamposConfirmados(fora));
    }

    // Status diferente de T nao vira contrato ativo por acidente.
    [Fact]
    public void Status_nao_aprovado_continua_recusado()
    {
        var encerrado = Snapshot("AC3S", "S", "40") with { StatusContrato = "X" };
        Assert.Throws<DecisaoFuncionalPendenteSapException>(
            () => MapeadorContratoAcmGuardian.MapearCamposConfirmados(encerrado));
    }

    private static AcmInboundWindowOptions Janela(params string[] excluidos) => new()
    {
        Enabled = true,
        WindowId = "a1b2c3d4e5f60718293a4b5c6d7e8f90",
        AuthorizationReference = "R170-TESTE",
        NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        UntilUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        Centro = "3011",
        Item = null,
        AllowedSapPartners = ["0020000051"],
        AllowedSapMaterials = ["000000000007000002"],
        ExcludedContractNumbers = excluidos
    };

    // A janela R57 usa a mesma regra: venda entra, mistura nao, e a operacao nao decide.
    [Fact]
    public void A_janela_r57_admite_venda_e_recusa_mistura()
    {
        var janela = Janela();
        Assert.True(janela.SnapshotPermitido(Snapshot("AC3S", "S", "40")));
        Assert.True(janela.SnapshotPermitido(Snapshot("AC3S", "S", "99")));
        Assert.False(janela.SnapshotPermitido(Snapshot("AC3S", "E", "40")));
        Assert.False(janela.SnapshotPermitido(Snapshot("AC3P", "S", "10")));
    }

    // Contratos historicos seguem bloqueados, mesmo em combinacao valida: nada de replay.
    [Theory]
    [InlineData("1031")]
    [InlineData("1160")]
    public void Historicos_e_excluidos_continuam_bloqueados(string numero)
    {
        var historico = Snapshot("AC3S", "S", "40") with { NumeroContrato = numero };
        Assert.False(Janela("1160").SnapshotPermitido(historico));
    }
}
