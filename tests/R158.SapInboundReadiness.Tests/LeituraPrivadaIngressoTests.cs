using FugaTES.WinForms.Monitor;

namespace R158.SapInboundReadiness.Tests;

/// <summary>
/// A API PRIVADA DE LEITURA TEM UM CANAL POR AMBIENTE, E AS DUAS PONTAS PRECISAM CONCORDAR.
///
/// O receptor serve a API numa porta derivada do ingresso aprovado (DEV110 em 18762, QAS120 em
/// 18763); o desktop chega por um tunel SSH que SO aceita as portas que conhece. As duas listas
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
    [InlineData(18763)]  // QAS120
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
    [InlineData(18764)]
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
        var o = Opcoes(18763);
        o = oQueMuda switch
        {
            "host" => o with { Host = "10.0.0.9" },
            "usuario" => o with { User = "outro" },
            _ => o with { Enabled = false },
        };

        Assert.Throws<PhysicalConnectionException>(() => LocalReadTunnel.BuildArguments(o));
    }
}
