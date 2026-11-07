using FugaTES.Aplicacao.Configuracao;
using FugaTES.HostIntegracao;
using Microsoft.Extensions.Configuration;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O ARQUIVO DE RUNTIME DO MM E O DO AMBIENTE DO PROCESSO.
///
/// Ate 06/11/2026 o Program.cs lia "/etc/fugates/mm/runtime.json" fixo. Com dois ambientes isso
/// era uma armadilha silenciosa: um segundo motor MM leria a configuracao do DEV - banco, papel e
/// diretorio de estado - por mais que a unidade dele declarasse outra coisa.
///
/// E declarar nao adiantaria: aquele AddJsonFile entra DEPOIS das variaveis de ambiente, e a
/// ultima fonte vence. A unidade do QAS poderia dizer fugates_qas em Environment= e gravar em
/// fugates_dev, com credencial valida e sem erro nenhum.
///
/// Estes testes fixam a derivacao pelo par SAP, que e a mesma de AcmEnvironmentBinding usada no
/// Program. Eles nao abrem arquivo: o que importa e QUAL ambiente sai do par.
/// </summary>
public sealed class MmRuntimeCaminhoAmbienteTests
{
    private static IConfiguration Config(string? origem, string? client)
    {
        var dados = new Dictionary<string, string?>();
        if (origem is not null) dados["Sap:BaseUrl"] = origem;
        if (client is not null) dados["Sap:SapClient"] = client;
        return new ConfigurationBuilder().AddInMemoryCollection(dados).Build();
    }

    /// <summary>
    /// SEM PAR SAP, CONTINUA DEV110. E o motor que roda hoje: a unidade dele nao declara
    /// Sap:BaseUrl nenhum, e ela nao pode mudar de comportamento por causa desta correcao.
    /// </summary>
    [Fact]
    public void Sem_par_sap_o_ambiente_continua_o_dev()
        => Assert.Equal("DEV110", AcmEnvironmentBinding.Sap(Config(null, null)).Nome);

    /// <summary>Cada par aprovado leva ao seu ambiente, e e dele que o caminho sai.</summary>
    [Theory]
    [InlineData("https://vhfufds4ci.sap.fugacouros.com.br:44300", "110", "DEV110")]
    [InlineData("https://vhfufqs4ci.sap.fugacouros.com.br:44300", "120", "QAS120")]
    public void O_par_sap_decide_o_ambiente(string origem, string client, string esperado)
        => Assert.Equal(esperado, AcmEnvironmentBinding.Sap(Config(origem, client)).Nome);

    /// <summary>
    /// O CRUZAMENTO NAO VIRA AMBIENTE NENHUM, e portanto nao escolhe caminho: host de um com
    /// client do outro e recusado em vez de cair no DEV por omissao.
    /// </summary>
    [Theory]
    [InlineData("https://vhfufds4ci.sap.fugacouros.com.br:44300", "120")]
    [InlineData("https://vhfufqs4ci.sap.fugacouros.com.br:44300", "110")]
    [InlineData("https://outro.exemplo.com.br:44300", "110")]
    public void Par_cruzado_e_recusado(string origem, string client)
        => Assert.Equal("ACM_SAP_ENVIRONMENT_PAIR_REJECTED",
            Record.Exception(() => AcmEnvironmentBinding.Sap(Config(origem, client)))?.Message);

    /// <summary>
    /// Cada ambiente tem o SEU banco de MM. Se os dois caissem no mesmo, o segundo motor gravaria
    /// no banco do primeiro - que e o que o caminho fixo fazia acontecer.
    /// </summary>
    [Theory]
    [InlineData("DEV110", "fugates_dev", "fugates_mm_app")]
    [InlineData("QAS120", "fugates_qas", "fugates_qas_mm_app")]
    public void Cada_ambiente_tem_o_seu_banco_de_mm(string nome, string banco, string papel)
    {
        var ambiente = AmbientesBanco.PorNome(nome)!;

        Assert.Equal(banco, ambiente.Banco);
        Assert.Equal(papel, ambiente.PapelMm);
    }
}
