using FugaTES.Aplicacao.Configuracao;
using Microsoft.Extensions.Configuration;

namespace FugaTES.Testes;

/// <summary>
/// Corte do cadastro automatico do ACM. Pedido do operador em 05/11/2026: "daqui pra frente, dos
/// proximos que vierem" - a fila tinha cerca de quarenta contratos travados, e ele quis que o motor
/// deixasse de persegui-los.
///
/// E corte por data de RECEBIMENTO, nao lista de numeros: o pedido era temporal, e enumerar os
/// quarenta satisfaria hoje e exigiria manutencao amanha.
/// </summary>
public sealed class AcmCorteAutomaticoTests
{
    private static readonly DateTimeOffset Corte = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    private static AcmAutomaticoOptions Escopo(DateTimeOffset? corte = null) => new()
    {
        Enabled = true,
        AuthorizationReference = "R170-OPERADOR-CONTINUO",
        Centros = ["3011", "3008"],
        RecebidoDesdeUtc = corte
    };

    [Fact]
    public void Sem_corte_tudo_passa()
    {
        var escopo = Escopo();
        Assert.True(escopo.EhValida());
        Assert.True(escopo.RecebimentoNoCorte(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        // Inclusive recebimento sem data: e o comportamento anterior, preservado de proposito.
        Assert.True(escopo.RecebimentoNoCorte(null));
    }

    [Fact]
    public void Recebimento_no_instante_do_corte_passa()
        => Assert.True(Escopo(Corte).RecebimentoNoCorte(Corte));

    [Fact]
    public void Recebimento_depois_do_corte_passa()
        => Assert.True(Escopo(Corte).RecebimentoNoCorte(Corte.AddSeconds(1)));

    [Fact]
    public void Recebimento_antes_do_corte_nao_passa()
        => Assert.False(Escopo(Corte).RecebimentoNoCorte(Corte.AddSeconds(-1)));

    // "Nao sei quando chegou" nao pode valer como "chegou depois do corte": seria deixar entrar
    // justamente o que nao se consegue datar.
    [Fact]
    public void Recebimento_sem_data_nao_passa_quando_ha_corte()
        => Assert.False(Escopo(Corte).RecebimentoNoCorte(null));

    // O que o operador quis dizer com "os proximos que vierem": contrato REENTREGUE pelo SAP
    // depois do corte entra, ainda que o numero seja antigo. O corte e sobre o recebimento.
    [Fact]
    public void Contrato_antigo_reentregue_depois_do_corte_entra()
        => Assert.True(Escopo(Corte).RecebimentoNoCorte(Corte.AddDays(3)));

    [Fact]
    public void Corte_fora_de_utc_invalida_o_recorte()
        => Assert.False(Escopo(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.FromHours(-3))).EhValida());

    [Fact]
    public void Corte_em_utc_mantem_o_recorte_valido()
        => Assert.True(Escopo(Corte).EhValida());

    /// <summary>
    /// O instante COMO O SCRIPT O ESCREVE tem de ligar com offset ZERO.
    ///
    /// Isto nao e detalhe de formato: EhValida() exige Offset == TimeSpan.Zero. Um texto que
    /// ligasse com outro offset invalidaria o recorte INTEIRO e o motor pararia de cadastrar
    /// qualquer contrato - fail-closed, mas uma surpresa pessima, e por uma letra no fim da string.
    /// O 'Z' vem de `date -u +%Y-%m-%dT%H:%M:%S.%6NZ` em publicar-corte-acm.sh.
    /// </summary>
    [Theory]
    [InlineData("2026-11-05T00:30:11.360468Z")]
    [InlineData("2026-11-05T00:30:11Z")]
    [InlineData("2026-11-05T00:30:11.000000Z")]
    public void O_instante_como_o_script_escreve_liga_em_utc(string texto)
    {
        var escopo = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MotorIntegracao:AcmAutomatico:Enabled"] = "true",
            ["MotorIntegracao:AcmAutomatico:AuthorizationReference"] = "R170-OPERADOR-CONTINUO",
            ["MotorIntegracao:AcmAutomatico:Centros:0"] = "3011",
            ["MotorIntegracao:AcmAutomatico:Centros:1"] = "3008",
            ["MotorIntegracao:AcmAutomatico:RecebidoDesdeUtc"] = texto
        }).Build().GetSection("MotorIntegracao:AcmAutomatico").Get<AcmAutomaticoOptions>();

        Assert.NotNull(escopo);
        Assert.NotNull(escopo!.RecebidoDesdeUtc);
        Assert.Equal(TimeSpan.Zero, escopo.RecebidoDesdeUtc!.Value.Offset);
        Assert.True(escopo.EhValida(), "o corte escrito pelo script tem de manter o recorte valido");
        Assert.False(escopo.RecebimentoNoCorte(escopo.RecebidoDesdeUtc.Value.AddTicks(-1)));
        Assert.True(escopo.RecebimentoNoCorte(escopo.RecebidoDesdeUtc.Value));
    }

    // O corte NAO substitui as outras regras: ele e mais um "e", nunca um atalho. Contrato recente
    // de centro de fora continua reprovando.
    [Fact]
    public void Corte_nao_afrouxa_o_resto_do_recorte()
    {
        var escopo = Escopo(Corte);
        var snapshot = AcmGuardianReferenceMapperTests.Snapshot() with
        {
            NumeroContrato = "0000001300", Centro = "3020", TipoPesagem = "E",
            DescricaoTipoContrato = "Compra", QuantidadeContrato = 10m, IdParceiro = "20000051"
        };
        Assert.True(escopo.RecebimentoNoCorte(Corte.AddDays(1)));
        Assert.False(escopo.SnapshotPermitido(snapshot));
    }
}
