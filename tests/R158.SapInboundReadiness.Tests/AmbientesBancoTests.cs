using FugaTES.Aplicacao.Configuracao;
using FugaTES.Callback.Http;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O banco deixa de estar escrito no codigo - e o par banco+papel e indivisivel.
///
/// Ate 06/11/2026 "fugates_dev" estava como literal em cerca de trinta lugares do codigo de
/// producao, quase todos em guardas que lancam. Nao era descuido: e o que impede o sistema de
/// gravar num banco que ninguem aprovou. O que muda e que agora ha dois nomes na lista.
///
/// POR QUE O PAR, e nao banco de um lado e papel de outro: papel no PostgreSQL e do CLUSTER e
/// nao do banco. Conferir "banco aprovado" e "papel aprovado" em separado deixaria passar o
/// fugates_qas com o fugates_acm_hml_app - credencial valida, ambiente errado.
///
/// SO O CAMINHO SD FOI AMPLIADO. O receptor Toledo, o ACM e o MM continuam no DEV110, e cada
/// guarda recebe os ambientes que aceita em vez de consultar a lista inteira. Ampliar e
/// acrescentar um ambiente NA CHAMADA.
/// </summary>
public sealed class AmbientesBancoTests
{
    [Fact]
    public void Sao_dois_ambientes_e_nem_um_a_mais()
        => Assert.Equal(2, AmbientesBanco.Aprovados.Length);

    [Fact]
    public void Os_dois_bancos_e_os_papeis_nao_se_repetem()
    {
        Assert.NotEqual(AmbientesBanco.Dev110.Banco, AmbientesBanco.Qas120.Banco);
        // Nenhum papel aparece nos dois: credencial do QAS nao abre o banco do DEV, e vice-versa.
        Assert.Empty(AmbientesBanco.Dev110.Papeis.Intersect(AmbientesBanco.Qas120.Papeis));
    }

    [Theory]
    [InlineData("fugates_dev", "fugates_acm_hml_app", "DEV110")]
    [InlineData("fugates_dev", "fugates_sd_app", "DEV110")]
    [InlineData("fugates_qas", "fugates_qas_acm_app", "QAS120")]
    [InlineData("fugates_qas", "fugates_qas_sd_app", "QAS120")]
    public void O_par_correto_encontra_o_seu_ambiente(string banco, string papel, string nome)
        => Assert.Equal(nome, AmbientesBanco.Por(banco, papel)?.Nome);

    /// <summary>
    /// O CRUZAMENTO: banco de um ambiente com papel do outro nao e ambiente nenhum. E o caso que
    /// uma conferencia em separado deixaria passar.
    /// </summary>
    [Theory]
    [InlineData("fugates_qas", "fugates_acm_hml_app")]
    [InlineData("fugates_dev", "fugates_qas_acm_app")]
    [InlineData("fugates_qas", "fugates_sd_app")]
    [InlineData("fugates_dev", "fugates_qas_sd_app")]
    [InlineData("fugates_outro", "fugates_acm_hml_app")]
    [InlineData("fugates_dev", "postgres")]
    [InlineData(null, "fugates_acm_hml_app")]
    [InlineData("fugates_dev", null)]
    public void O_par_cruzado_ou_desconhecido_nao_e_ambiente(string? banco, string? papel)
        => Assert.Null(AmbientesBanco.Por(banco, papel));

    /// <summary>
    /// Sem ambientes informados vale o DEV110, e so ele. E esse padrao que fez nenhum chamador de
    /// hoje mudar de comportamento quando o QAS120 apareceu.
    /// </summary>
    [Fact]
    public void Sem_ambiente_informado_vale_so_o_dev()
    {
        Assert.NotNull(AmbientesBanco.Por("fugates_dev", "fugates_acm_hml_app", AmbientesBanco.Dev110));
        Assert.Null(AmbientesBanco.Por("fugates_qas", "fugates_qas_acm_app", AmbientesBanco.Dev110));
    }

    // --- o vinculo dedicado, que e a guarda do receptor --------------------------------------

    private static string Cadeia(string banco, string papel)
        => $"Host=127.0.0.1;Port=5433;Database={banco};Search Path=fugates;Username={papel};Enlist=false;Passfile=/etc/fugates/pg.pass";

    [Fact]
    public void O_vinculo_do_dev_continua_valendo_sem_nada_informado()
        => Assert.Contains("fugates_dev",
            ToledoCallbackComposition.ValidateDedicatedBinding(Cadeia("fugates_dev", "fugates_acm_hml_app")),
            StringComparison.Ordinal);

    /// <summary>
    /// O QAS so passa quando o chamador o ACEITA. Sem isso, a guarda recusa - e e assim que o
    /// receptor Toledo e o ACM continuam presos ao DEV110 mesmo com o registro conhecendo os dois.
    /// </summary>
    [Fact]
    public void O_vinculo_do_qas_so_passa_para_quem_o_aceita()
    {
        var cadeia = Cadeia("fugates_qas", "fugates_qas_acm_app");

        Assert.Throws<InvalidOperationException>(
            () => ToledoCallbackComposition.ValidateDedicatedBinding(cadeia));

        Assert.Contains("fugates_qas",
            ToledoCallbackComposition.ValidateDedicatedBinding(cadeia, AmbientesBanco.Dev110, AmbientesBanco.Qas120),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// O LIMITE: mesmo para quem aceita os dois, o par cruzado e recusado. Esta e a razao de a
    /// guarda conferir o par em vez de duas listas.
    /// </summary>
    [Theory]
    [InlineData("fugates_qas", "fugates_acm_hml_app")]
    [InlineData("fugates_dev", "fugates_qas_acm_app")]
    public void Mesmo_aceitando_os_dois_o_par_cruzado_e_recusado(string banco, string papel)
        => Assert.Throws<InvalidOperationException>(
            () => ToledoCallbackComposition.ValidateDedicatedBinding(
                Cadeia(banco, papel), AmbientesBanco.Dev110, AmbientesBanco.Qas120));

    /// <summary>
    /// E o resto da guarda nao afrouxou: porta, schema, host e passfile continuam exigidos.
    /// </summary>
    [Theory]
    [InlineData("Host=127.0.0.1;Port=5432;Database=fugates_qas;Search Path=fugates;Username=fugates_qas_acm_app;Enlist=false;Passfile=/etc/fugates/pg.pass")]
    [InlineData("Host=10.0.0.9;Port=5433;Database=fugates_qas;Search Path=fugates;Username=fugates_qas_acm_app;Enlist=false;Passfile=/etc/fugates/pg.pass")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=fugates_qas;Search Path=public;Username=fugates_qas_acm_app;Enlist=false;Passfile=/etc/fugates/pg.pass")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=fugates_qas;Search Path=fugates;Username=fugates_qas_acm_app;Enlist=false;Password=emtextoclaro")]
    public void O_resto_da_guarda_continua_igual(string cadeia)
        => Assert.Throws<InvalidOperationException>(
            () => ToledoCallbackComposition.ValidateDedicatedBinding(
                cadeia, AmbientesBanco.Dev110, AmbientesBanco.Qas120));
}
