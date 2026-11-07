namespace FugaTES.Aplicacao.Configuracao;

/// <summary>Um canal da API privada de leitura: o ambiente, a frase de aprovação e a porta.</summary>
public sealed record LeituraPrivadaIngresso(string Ambiente, string Aprovacao, int PortaRemota);

/// <summary>
/// OS CANAIS DA API PRIVADA DE LEITURA, NUM LUGAR SÓ.
///
/// Os três campos valem JUNTOS: a porta é a identidade do canal — é por ela que o túnel do desktop
/// chega — e a frase de aprovação é o que o operador declara. Aceitar a frase do DEV numa porta de
/// QAS seria servir dados de um ambiente por um canal autorizado para outro.
///
/// POR QUE AQUI, E NÃO EM CADA LADO. Esta decisão vive em três binários diferentes: o receptor
/// (que serve), o túnel do desktop (que chega) e a sessão unificada (que valida o perfil antes de
/// abrir o túnel). Em 06/11/2026 eu converti dois, escrevi em comentário que eram "duas listas, a
/// mesma decisão em dois lugares" — e eram três. O terceiro exigia 18762 e recusou o QAS120 com
/// "Binding SSH de leitura ausente ou inválido", uma mensagem que não aponta para porta nenhuma.
///
/// Contar de cabeça quantos lugares repetem uma regra é o mesmo erro de enumerar de memória as
/// diretivas de uma unidade: parece derivação e é lembrança. Com a lista aqui, acrescentar um
/// ambiente é um lugar só, e quem não a usar aparece numa busca.
///
/// A PORTA LOCAL DO TÚNEL (18761) NÃO ENTRA NESTA LISTA. Ela é a ponta de cá, fixa por desenho —
/// o túnel sempre encaminha 127.0.0.1:18761 para a porta remota do ambiente.
/// </summary>
public static class LeituraPrivadaIngressos
{
    /// <summary>A ponta LOCAL do túnel, igual para todos os ambientes.</summary>
    public const int PortaLocal = 18761;

    public static readonly LeituraPrivadaIngresso Dev110 =
        new("DEV110", "FUGATES-UNIFIED-POSTGRESQL-DEV", 18762);

    /// <summary>
    /// QAS120. A porta é 18764 e não 18763: aquela é do serviço fugates-acm-manual-retry, o que
    /// só o inventário da VM mostrou — a primeira escolha colidia com um serviço em uso.
    /// </summary>
    public static readonly LeituraPrivadaIngresso Qas120 =
        new("QAS120", "FUGATES-UNIFIED-POSTGRESQL-QAS120", 18764);

    public static readonly LeituraPrivadaIngresso[] Aprovados = [Dev110, Qas120];

    /// <summary>O canal deste par, ou nulo quando a frase não é a daquele ambiente.</summary>
    public static LeituraPrivadaIngresso? Por(string? ambiente, string? aprovacao)
        => Array.Find(Aprovados, i => i.Ambiente == ambiente && i.Aprovacao == aprovacao);

    /// <summary>O canal daquele ambiente, sem olhar a frase. Para quem só precisa da porta.</summary>
    public static LeituraPrivadaIngresso? PorAmbiente(string? ambiente)
        => Array.Find(Aprovados, i => i.Ambiente == ambiente);

    /// <summary>
    /// Esta porta remota é de algum canal aprovado? Pergunta mais fraca do que <see cref="Por"/>,
    /// e serve a quem vê a porta sem saber o ambiente — o construtor de argumentos do túnel.
    /// </summary>
    public static bool EhPortaRemotaAprovada(int porta)
        => porta == PortaLocal || Array.Exists(Aprovados, i => i.PortaRemota == porta);
}
