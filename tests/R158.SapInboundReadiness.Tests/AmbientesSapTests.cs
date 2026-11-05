using FugaTES.Aplicacao.Configuracao;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// Os ambientes do SAP. Criados em 05/11/2026 para o FugaTES passar a falar tambem com o QAS120,
/// sem perder a propriedade que o codigo tinha quando havia um ambiente so: nada sai para um SAP
/// que ninguem aprovou.
///
/// O operador confirmou que as CREDENCIAIS dos dois ambientes sao as mesmas. A senha, portanto,
/// nao separa nada - estes testes guardam a unica coisa que separa.
/// </summary>
public sealed class AmbientesSapTests
{
    private const string Dev = "https://vhfufds4ci.sap.fugacouros.com.br:44300";
    private const string Qas = "https://vhfufqs4ci.sap.fugacouros.com.br:44300";

    [Fact]
    public void Os_dois_ambientes_sao_aprovados()
    {
        Assert.True(AmbientesSap.EhAprovado(Dev, "110"));
        Assert.True(AmbientesSap.EhAprovado(Qas, "120"));
    }

    [Fact]
    public void O_nome_do_ambiente_vem_do_par()
    {
        Assert.Equal("DEV110", AmbientesSap.Por(Dev, "110")!.Nome);
        Assert.Equal("QAS120", AmbientesSap.Por(Qas, "120")!.Nome);
    }

    /// <summary>
    /// O PAR E INDIVISIVEL. Host de um ambiente com client do outro nao existe - e se passasse,
    /// gravaria no sistema errado com a credencial que serve aos dois.
    /// </summary>
    [Theory]
    [InlineData(Dev, "120")]
    [InlineData(Qas, "110")]
    public void Host_de_um_com_client_do_outro_nao_passa(string origem, string client)
        => Assert.False(AmbientesSap.EhAprovado(origem, client));

    /// <summary>
    /// As duas URLs diferem por UMA letra: "ds4ci" e "qs4ci". E o tipo de diferenca que passa
    /// despercebida numa revisao, e por isso esta fixada num teste.
    /// </summary>
    [Fact]
    public void As_duas_urls_diferem_por_uma_letra()
    {
        Assert.Equal(Dev.Replace("ds4ci", "qs4ci"), Qas);
        Assert.NotEqual(Dev, Qas);
    }

    [Theory]
    [InlineData("https://outro.sap.fugacouros.com.br:44300", "110")]
    [InlineData("http://vhfufds4ci.sap.fugacouros.com.br:44300", "110")]   // sem TLS
    [InlineData("https://vhfufds4ci.sap.fugacouros.com.br", "110")]        // sem a porta
    [InlineData("https://vhfufds4ci.sap.fugacouros.com.br:44301", "110")]  // outra porta
    [InlineData(Dev, "100")]
    [InlineData(Dev, "")]
    [InlineData("", "110")]
    [InlineData(null, null)]
    public void Qualquer_outra_combinacao_e_recusada(string? origem, string? client)
        => Assert.False(AmbientesSap.EhAprovado(origem, client));

    [Fact]
    public void Nao_ha_ambiente_repetido_nem_par_ambiguo()
    {
        Assert.Equal(AmbientesSap.Aprovados.Length,
            AmbientesSap.Aprovados.Select(a => a.Nome).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(AmbientesSap.Aprovados.Length,
            AmbientesSap.Aprovados.Select(a => a.Origem + "|" + a.SapClient).Distinct(StringComparer.Ordinal).Count());
    }

    // Todo ambiente aprovado e https, sem credencial embutida e sem query - as mesmas exigencias
    // que cada guarda fazia quando o endereco era constante.
    [Fact]
    public void Todo_ambiente_aprovado_e_uma_origem_limpa_em_https()
        => Assert.All(AmbientesSap.Aprovados, a =>
        {
            var u = new Uri(a.Origem);
            Assert.Equal("https", u.Scheme);
            Assert.Empty(u.UserInfo);
            Assert.Empty(u.Query);
            Assert.Equal(a.Origem, u.GetLeftPart(UriPartial.Authority));
            Assert.Matches("^[0-9]{3}$", a.SapClient);
        });
}
