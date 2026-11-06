using FugaTES.HostIntegracao;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// O DIRETORIO DE CREDENCIAIS E O NOME DA UNIDADE, E HA UMA UNIDADE POR AMBIENTE.
///
/// O systemd monta as credenciais em /run/credentials/&lt;nome da unidade&gt;. Ate 06/11/2026 o motor
/// aceitava um nome so, escrito como constante, e o motor do QAS120 - que roda numa unidade
/// propria, porque duas nao podem compartilhar uma - recusava subir com
/// SD_MOTOR_CREDENCIAL_DIRETORIO_INVALIDO lendo o diretorio CERTO: o dele.
///
/// Estes testes fixam as duas metades: que os dois ambientes existem na lista, e que a lista nao
/// virou "qualquer diretorio serve" no caminho.
/// </summary>
public sealed class MotorSdCredenciaisAmbienteTests
{
    /// <summary>Cada ambiente tem a SUA unidade, e o diretorio sai do nome dela.</summary>
    [Theory]
    [InlineData("fugates-sd-protocolo.service")]
    [InlineData("fugates-sd-protocolo-qas.service")]
    public void A_unidade_de_cada_ambiente_esta_aprovada(string unidade)
    {
        Assert.Contains(unidade, MotorSdCredenciais.ServicosAprovados);
        Assert.Contains("/run/credentials/" + unidade, MotorSdCredenciais.DiretoriosAprovados);
    }

    /// <summary>
    /// Os dois ambientes tem diretorios DIFERENTES. Se um dia os nomes colidissem, os dois
    /// motores leriam as credenciais do mesmo lugar e a separacao de ambientes seria so aparente.
    /// </summary>
    [Fact]
    public void Os_ambientes_nao_compartilham_diretorio()
    {
        Assert.Equal(MotorSdCredenciais.DiretoriosAprovados.Count,
                     MotorSdCredenciais.DiretoriosAprovados.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, MotorSdCredenciais.DiretoriosAprovados.Count);
    }

    /// <summary>
    /// A lista nao e um prefixo solto: so caminhos exatos de /run/credentials entram, e um nome
    /// de unidade qualquer nao esta nela. Aceitar os dois ambientes nao podia virar aceitar
    /// qualquer um.
    /// </summary>
    [Theory]
    [InlineData("/run/credentials/fugates-toledo-callback.service")]
    [InlineData("/run/credentials/qualquer-coisa.service")]
    [InlineData("/run/credentials")]
    [InlineData("/tmp/fugates-sd-protocolo.service")]
    [InlineData("/run/credentials/fugates-sd-protocolo.service/..")]
    public void Diretorio_que_nao_e_de_motor_aprovado_fica_de_fora(string caminho)
        => Assert.DoesNotContain(caminho, MotorSdCredenciais.DiretoriosAprovados);

    /// <summary>
    /// Todo diretorio aprovado mora em /run/credentials - e onde o systemd monta, e nenhum outro
    /// lugar deveria aparecer aqui nem por engano de digitacao.
    /// </summary>
    [Fact]
    public void Todos_os_diretorios_ficam_sob_run_credentials()
        => Assert.All(MotorSdCredenciais.DiretoriosAprovados,
            d => Assert.StartsWith("/run/credentials/", d, StringComparison.Ordinal));

    /// <summary>
    /// A constante antiga continua apontando para a unidade do DEV110. Ela e referenciada por
    /// quem ja existia; mudar o que ela significa seria pior do que remove-la.
    /// </summary>
    [Fact]
    public void A_constante_do_dev_continua_sendo_a_do_dev()
        => Assert.Equal("fugates-sd-protocolo.service", MotorSdCredenciais.Servico);
}
