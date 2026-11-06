using FugaTES.Aplicacao.Configuracao;
using FugaTES.Callback.Http;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O RECEPTOR GRAVA NO BANCO DO SEU PROPRIO AMBIENTE.
///
/// Ate 06/11/2026 ValidateDedicatedBinding aceitava so o DEV110, escrito como padrao. Com dois
/// ambientes isso deixou de ser uma trava e virou um impedimento: o receptor do QAS120 nao subia,
/// recusando o banco DELE.
///
/// A correcao NAO foi "aceitar os dois". Isso seria pior do que o que havia, porque deixaria passar
/// o par cruzado - um receptor de QAS120 gravando no banco do DEV, com credencial valida e sem erro
/// nenhum. O ambiente e DERIVADO do par SAP do processo, e so aquele banco serve.
///
/// Estes testes fixam as duas metades: cada ambiente com o seu banco passa, e o cruzamento recusa.
/// </summary>
public sealed class ToledoVinculoAmbienteTests
{
    private static string Cadeia(string banco, string papel)
        => $"Host=127.0.0.1;Port=5433;Database={banco};Search Path=fugates;Username={papel};"
         + "Enlist=false;Passfile=/etc/fugates/pg.pass";

    /// <summary>Cada ambiente com o SEU banco e o SEU papel ACM.</summary>
    [Theory]
    [InlineData("DEV110", "fugates_dev", "fugates_acm_hml_app")]
    [InlineData("QAS120", "fugates_qas", "fugates_qas_acm_app")]
    public void O_banco_do_proprio_ambiente_passa(string nome, string banco, string papel)
    {
        var ambiente = Array.Find(AmbientesBanco.Aprovados, a => a.Nome == nome)!;

        var cs = ToledoCallbackComposition.ValidateDedicatedBinding(Cadeia(banco, papel), ambiente);

        Assert.Contains(banco, cs, StringComparison.Ordinal);
    }

    /// <summary>
    /// O CRUZAMENTO. Cada ponta e valida por si - e e justamente isso que o torna caro: nada
    /// falharia em tempo de execucao, e a pesagem ficaria num ambiente e a devolutiva no outro.
    /// </summary>
    [Theory]
    [InlineData("QAS120", "fugates_dev", "fugates_acm_hml_app")]
    [InlineData("DEV110", "fugates_qas", "fugates_qas_acm_app")]
    public void Ambiente_de_um_com_banco_do_outro_e_recusado(string nome, string banco, string papel)
    {
        var ambiente = Array.Find(AmbientesBanco.Aprovados, a => a.Nome == nome)!;

        var erro = Record.Exception(
            () => ToledoCallbackComposition.ValidateDedicatedBinding(Cadeia(banco, papel), ambiente));

        Assert.Equal("TOLEDO_DEDICATED_5433_BINDING_REQUIRED", erro?.Message);
    }

    /// <summary>Banco certo, papel do outro ambiente: o par continua conferido JUNTO.</summary>
    [Theory]
    [InlineData("QAS120", "fugates_qas", "fugates_acm_hml_app")]
    [InlineData("DEV110", "fugates_dev", "fugates_qas_acm_app")]
    [InlineData("QAS120", "fugates_qas", "fugates_qas_sd_app")]
    public void Papel_que_nao_e_o_acm_daquele_ambiente_e_recusado(string nome, string banco, string papel)
    {
        var ambiente = Array.Find(AmbientesBanco.Aprovados, a => a.Nome == nome)!;

        var erro = Record.Exception(
            () => ToledoCallbackComposition.ValidateDedicatedBinding(Cadeia(banco, papel), ambiente));

        Assert.Equal("TOLEDO_DEDICATED_5433_BINDING_REQUIRED", erro?.Message);
    }

    /// <summary>
    /// SEM AMBIENTE INFORMADO, CONTINUA SENDO O DEV110. Quem nao foi convertido nao muda de
    /// comportamento por causa desta correcao.
    /// </summary>
    [Fact]
    public void Sem_ambiente_o_padrao_continua_o_dev()
    {
        Assert.Contains("fugates_dev",
            ToledoCallbackComposition.ValidateDedicatedBinding(Cadeia("fugates_dev", "fugates_acm_hml_app")),
            StringComparison.Ordinal);

        Assert.Equal("TOLEDO_DEDICATED_5433_BINDING_REQUIRED",
            Record.Exception(() => ToledoCallbackComposition.ValidateDedicatedBinding(
                Cadeia("fugates_qas", "fugates_qas_acm_app")))?.Message);
    }

    /// <summary>
    /// As exigencias que nao sao de ambiente seguem valendo: porta, schema, host, Enlist e
    /// passfile. Alargar o ambiente nao podia afrouxar o resto.
    /// </summary>
    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=fugates_qas;Search Path=fugates;Username=fugates_qas_acm_app;Enlist=false;Passfile=/etc/fugates/pg.pass")]
    [InlineData("Host=10.0.0.9;Port=5433;Database=fugates_qas;Search Path=fugates;Username=fugates_qas_acm_app;Enlist=false;Passfile=/etc/fugates/pg.pass")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=fugates_qas;Search Path=public;Username=fugates_qas_acm_app;Enlist=false;Passfile=/etc/fugates/pg.pass")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=fugates_qas;Search Path=fugates;Username=fugates_qas_acm_app;Passfile=/etc/fugates/pg.pass")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=fugates_qas;Search Path=fugates;Username=fugates_qas_acm_app;Enlist=false;Password=x")]
    public void As_demais_exigencias_continuam(string cadeia)
        => Assert.Equal("TOLEDO_DEDICATED_5433_BINDING_REQUIRED",
            Record.Exception(() => ToledoCallbackComposition.ValidateDedicatedBinding(
                cadeia, AmbientesBanco.Qas120))?.Message);
}
