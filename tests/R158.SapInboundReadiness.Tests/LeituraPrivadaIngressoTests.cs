using FugaTES.WinForms.Monitor;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// A API PRIVADA DE LEITURA TEM UM CANAL POR AMBIENTE, E AS DUAS PONTAS PRECISAM CONCORDAR.
///
/// O receptor serve a API numa porta derivada do ingresso aprovado (DEV110 em 18762, QAS120 em
/// 18764); o desktop chega por um tunel SSH que SO aceita as portas que conhece. As duas listas
/// sao a mesma decisao escrita em dois binarios diferentes.
///
/// Acrescentar um ambiente num lado sem o outro deixa a tela sem conectar, e o sintoma que aparece
/// e "API privada indisponivel" - que manda procurar o servidor quando quem recusou foi o cliente.
/// Estes testes existem para que essa divergencia nao passe calada.
/// </summary>
public sealed class LeituraPrivadaIngressoTests
{
    private static LocalReadTunnelOptions Opcoes(int portaRemota) => new()
    {
        Enabled = true,
        Host = "192.168.17.40",
        User = "michael",
        IdentityFile = @"C:\ProgramData\FugaTES\agentic\ssh\chave",
        KnownHostsFile = @"C:\Users\TI\.ssh\known_hosts",
        RemoteReadPort = portaRemota,
    };

    /// <summary>As portas dos dois ambientes sao aceitas pelo tunel do desktop.</summary>
    [Theory]
    [InlineData(18762)]  // DEV110
    [InlineData(18764)]  // QAS120
    public void O_tunel_aceita_a_porta_de_cada_ambiente(int porta)
    {
        var argumentos = LocalReadTunnel.BuildArguments(Opcoes(porta));

        // O encaminhamento leva da ponta local fixa para a porta remota daquele ambiente.
        Assert.Contains("127.0.0.1:18761:127.0.0.1:" + porta, argumentos);
    }

    /// <summary>
    /// Porta fora da lista continua recusada. Alargar para o QAS nao podia virar "qualquer porta
    /// serve": o tunel e um encaminhamento para dentro da VM, e o destino e parte do que se
    /// autoriza.
    /// </summary>
    [Theory]
    [InlineData(18765)]
    [InlineData(18763)]  // fugates-acm-manual-retry: servico legitimo, e nao canal de leitura
    [InlineData(5433)]
    [InlineData(22)]
    [InlineData(18087)]
    [InlineData(0)]
    public void Porta_fora_da_lista_continua_recusada(int porta)
        => Assert.Throws<PhysicalConnectionException>(() => LocalReadTunnel.BuildArguments(Opcoes(porta)));

    /// <summary>
    /// As demais exigencias do tunel seguem valendo. Alargar a porta nao podia afrouxar host,
    /// usuario ou os caminhos de chave.
    /// </summary>
    [Theory]
    [InlineData("host")]
    [InlineData("usuario")]
    [InlineData("desligado")]
    public void As_demais_exigencias_do_tunel_continuam(string oQueMuda)
    {
        var o = Opcoes(18764);
        o = oQueMuda switch
        {
            "host" => o with { Host = "10.0.0.9" },
            "usuario" => o with { User = "outro" },
            _ => o with { Enabled = false },
        };

        Assert.Throws<PhysicalConnectionException>(() => LocalReadTunnel.BuildArguments(o));
    }
}

/// <summary>
/// A LISTA DOS CANAIS E UMA SO, E OS TRES LADOS A USAM.
///
/// Em 06/11/2026 a mesma regra estava escrita em tres lugares - receptor, tunel do desktop e
/// sessao unificada. Converti dois e escrevi em comentario que eram "duas listas": um numero que
/// nao conferi. O terceiro exigia 18762 e recusou o QAS120 com uma mensagem que nao menciona
/// porta nenhuma.
/// </summary>
public sealed class LeituraPrivadaListaUnicaTests
{
    [Theory]
    [InlineData("DEV110", 18762)]
    [InlineData("QAS120", 18764)]
    public void Cada_ambiente_tem_o_seu_canal(string ambiente, int porta)
        => Assert.Equal(porta,
            FugaTES.Aplicacao.Configuracao.LeituraPrivadaIngressos.PorAmbiente(ambiente)!.PortaRemota);

    /// <summary>A frase de aprovacao vale JUNTO com o ambiente, nunca sozinha.</summary>
    [Theory]
    [InlineData("DEV110", "FUGATES-UNIFIED-POSTGRESQL-QAS120")]
    [InlineData("QAS120", "FUGATES-UNIFIED-POSTGRESQL-DEV")]
    [InlineData("QAS120", "")]
    public void Frase_de_outro_ambiente_nao_abre_canal(string ambiente, string frase)
        => Assert.Null(FugaTES.Aplicacao.Configuracao.LeituraPrivadaIngressos.Por(ambiente, frase));

    /// <summary>Os canais nao compartilham porta - seria um ambiente servindo pelo outro.</summary>
    [Fact]
    public void Os_canais_nao_compartilham_porta()
    {
        var portas = Array.ConvertAll(
            FugaTES.Aplicacao.Configuracao.LeituraPrivadaIngressos.Aprovados, i => i.PortaRemota);
        Assert.Equal(portas.Length, portas.Distinct().Count());
    }

    /// <summary>
    /// 18763 NAO e canal de leitura: e do servico fugates-acm-manual-retry, conferido na VM. Foi
    /// a primeira escolha para o QAS e colidia com um servico em uso.
    /// </summary>
    [Theory]
    [InlineData(18763)]
    [InlineData(18765)]
    [InlineData(18087)]
    [InlineData(5433)]
    public void Porta_que_nao_e_canal_e_recusada(int porta)
        => Assert.False(FugaTES.Aplicacao.Configuracao.LeituraPrivadaIngressos.EhPortaRemotaAprovada(porta));

    /// <summary>A ponta LOCAL do tunel e aceita: ela e a de ca, fixa por desenho.</summary>
    [Fact]
    public void A_porta_local_do_tunel_e_aceita()
        => Assert.True(FugaTES.Aplicacao.Configuracao.LeituraPrivadaIngressos.EhPortaRemotaAprovada(
            FugaTES.Aplicacao.Configuracao.LeituraPrivadaIngressos.PortaLocal));
}
