using FugaTES.Aplicacao.Configuracao;
using FugaTES.HostIntegracao;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O MOTOR SD GRAVA NO BANCO DO SEU PROPRIO AMBIENTE, e nao em qualquer um aprovado.
///
/// Ate 06/11/2026 ele conectava no que estivesse configurado - sem conferir banco, papel, porta
/// ou schema. Com um ambiente so isso nunca apareceu, porque so havia um banco para onde ir.
///
/// Com dois, e o cruzamento mais caro possivel: um motor apontado para o SAP QAS120 e para o
/// banco do DEV gravaria a remessa num ambiente e a pesagem no outro, as duas com credencial
/// valida e sem erro nenhum. Ninguem descobre ate alguem procurar uma remessa que existe no
/// lugar errado.
///
/// Por isso o motor nao "aceita os dois": ele DERIVA do ambiente SAP o unico banco que serve.
/// </summary>
public sealed class MotorSdAmbienteBancoTests
{
    private static string Cadeia(string banco, string papel)
        => $"Host=127.0.0.1;Port=5433;Database={banco};Search Path=fugates;Username={papel};Enlist=false;Passfile=/etc/fugates/pg.pass";

    // ExigirBancoDoAmbiente, e nao ConfigurarServicos: a composicao recusa rodar fora do Linux
    // antes de chegar na regra, entao por ela a regra nao teria como ser exercitada aqui.
    private static Exception? Montar(AmbienteSap sap, string banco, string papel)
        => Record.Exception(() => SdProtocoloComposition.ExigirBancoDoAmbiente(Cadeia(banco, papel), sap));

    /// <summary>Cada ambiente com o SEU banco e o SEU papel: passa.</summary>
    [Theory]
    [InlineData("DEV110", "fugates_dev", "fugates_sd_app")]
    [InlineData("QAS120", "fugates_qas", "fugates_qas_sd_app")]
    public void O_banco_do_proprio_ambiente_passa(string nome, string banco, string papel)
    {
        var sap = Array.Find(AmbientesSap.Aprovados, a => a.Nome == nome)!;

        var erro = Montar(sap, banco, papel);

        Assert.True(erro is null || erro.Message != "SD_MOTOR_BANCO_NAO_CONFERE_COM_O_AMBIENTE",
            $"recusou o par correto: {erro?.Message}");
    }

    /// <summary>
    /// O CRUZAMENTO. SAP de um ambiente com banco do outro - as duas pontas validas em separado,
    /// e justamente por isso perigosas: nada falharia em tempo de execucao.
    /// </summary>
    [Theory]
    [InlineData("QAS120", "fugates_dev", "fugates_sd_app")]
    [InlineData("DEV110", "fugates_qas", "fugates_qas_sd_app")]
    public void Sap_de_um_ambiente_com_banco_do_outro_e_recusado(string nome, string banco, string papel)
    {
        var sap = Array.Find(AmbientesSap.Aprovados, a => a.Nome == nome)!;

        Assert.Equal("SD_MOTOR_BANCO_NAO_CONFERE_COM_O_AMBIENTE", Montar(sap, banco, papel)?.Message);
    }

    /// <summary>Banco certo, papel do outro ambiente: o par continua sendo conferido junto.</summary>
    [Theory]
    [InlineData("DEV110", "fugates_dev", "fugates_qas_sd_app")]
    [InlineData("QAS120", "fugates_qas", "fugates_sd_app")]
    [InlineData("DEV110", "fugates_dev", "fugates_acm_hml_app")]
    public void Papel_que_nao_e_o_do_sd_daquele_ambiente_e_recusado(string nome, string banco, string papel)
    {
        var sap = Array.Find(AmbientesSap.Aprovados, a => a.Nome == nome)!;

        Assert.Equal("SD_MOTOR_BANCO_NAO_CONFERE_COM_O_AMBIENTE", Montar(sap, banco, papel)?.Message);
    }

    /// <summary>O resto da guarda nasceu junto: porta, schema e host tambem sao exigidos.</summary>
    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=fugates_dev;Search Path=fugates;Username=fugates_sd_app")]
    [InlineData("Host=10.0.0.9;Port=5433;Database=fugates_dev;Search Path=fugates;Username=fugates_sd_app")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=fugates_dev;Search Path=public;Username=fugates_sd_app")]
    public void Porta_schema_e_host_continuam_exigidos(string cadeia)
    {
        var erro = Record.Exception(() => SdProtocoloComposition.ExigirBancoDoAmbiente(cadeia, AmbientesSap.Dev110));

        Assert.Equal("SD_MOTOR_BANCO_NAO_CONFERE_COM_O_AMBIENTE", erro?.Message);
    }
}
