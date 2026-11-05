using FugaTES.Aplicacao.Configuracao;
using FugaTES.WinForms.Runtime;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O ambiente no titulo da janela. A mesma publicacao vai para duas pastas, as telas sao
/// identicas e as credenciais dos dois ambientes sao as mesmas - nada no que se ve diz em qual
/// deles a pessoa esta.
/// </summary>
public sealed class AmbienteDesktopTests
{
    [Fact]
    public void O_titulo_leva_o_ambiente_e_o_sufixo()
        => Assert.Equal("FugaTES — " + AmbienteDesktop.Nome + " — v1.2.3",
            AmbienteDesktop.Titulo("v1.2.3"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sem_sufixo_o_titulo_termina_no_ambiente(string? sufixo)
        => Assert.Equal("FugaTES — " + AmbienteDesktop.Nome, AmbienteDesktop.Titulo(sufixo));

    /// <summary>
    /// Nos testes nao ha FugaTES.carrier.json ao lado do binario, entao o ambiente nao pode ser
    /// determinado - e o titulo ADMITE isso em vez de supor um. Supor DEV110 era o comportamento
    /// anterior, e e o que faria um desktop de QAS afirmar a coisa errada com toda a confianca.
    /// </summary>
    [Fact]
    public void Sem_perfil_o_titulo_nao_inventa_um_ambiente()
    {
        Assert.Equal(AmbienteDesktop.Desconhecido, AmbienteDesktop.Nome);
        Assert.DoesNotContain("DEV110", AmbienteDesktop.Titulo("v1"));
        Assert.DoesNotContain("QAS120", AmbienteDesktop.Titulo("v1"));
    }

    // Descobrir o ambiente nunca pode derrubar o programa: um titulo de janela nao e motivo para
    // nao abrir, e quem realmente fala com o SAP ja confere o perfil com erro visivel.
    [Fact]
    public void Descobrir_o_ambiente_nunca_lanca()
    {
        var excecao = Record.Exception(() => AmbienteDesktop.Titulo("v1"));
        Assert.Null(excecao);
    }

    [Fact]
    public void Todo_ambiente_aprovado_cabe_num_titulo_legivel()
        => Assert.All(AmbientesSap.Aprovados, a =>
        {
            Assert.DoesNotContain(" ", a.Nome);
            Assert.InRange(a.Nome.Length, 3, 12);
        });
}
