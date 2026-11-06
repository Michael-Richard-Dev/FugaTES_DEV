namespace FugaTES.Aplicacao.Configuracao;

/// <summary>
/// Um ambiente do PostgreSQL: o BANCO e os PAPEIS que valem nele. Os dois so valem JUNTOS.
/// </summary>
/// <param name="Nome">O mesmo nome do ambiente SAP correspondente - ver <see cref="AmbientesSap"/>.</param>
public sealed record AmbienteBanco(
    string Nome,
    string Banco,
    string PapelAcm,
    string PapelSd,
    string PapelLeitura,
    string PapelSapInbound,
    string PapelMm,
    string PapelDesktop)
{
    /// <summary>Todos os papeis deste ambiente, para a conferencia do par.</summary>
    public string[] Papeis => [PapelAcm, PapelSd, PapelLeitura, PapelSapInbound, PapelMm, PapelDesktop];

    public bool TemPapel(string? papel)
        => papel is not null && Array.Exists(Papeis, p => string.Equals(p, papel, StringComparison.Ordinal));
}

/// <summary>
/// Os bancos com que o FugaTES pode falar, no cluster 18/fugatesacmdev da VM (porta 5433).
///
/// Ate 06/11/2026 existia um so, e "fugates_dev" estava escrito como literal em cerca de TRINTA
/// lugares do codigo de producao, quase todos em guardas que lancam. Isso nao era descuido: e o
/// que impede o sistema de gravar num banco que ninguem aprovou. Esta lista preserva essa
/// propriedade; o que muda e que agora ha dois nomes nela.
///
/// O PAR E INDIVISIVEL, pela mesma razao do par origem+sap-client em <see cref="AmbientesSap"/>:
/// o banco do QAS com o papel do DEV nao e ambiente nenhum. Aqui isso importa ainda mais, porque
/// os papeis sao do CLUSTER e nao do banco - sem conferir o par, uma cadeia de conexao com o nome
/// do banco trocado passaria pela guarda e so falharia no servidor, se falhasse.
///
/// QUEM ACEITA O QUE E DECISAO DE CADA FLUXO, e nao desta lista. Em 06/11/2026 so o caminho SD
/// foi convertido: o ACM e o MM continuam presos ao DEV110, e e por isso que as guardas recebem
/// os ambientes que aceitam em vez de consultarem <see cref="Aprovados"/> por conta propria.
/// Ampliar e acrescentar um ambiente na chamada, deliberadamente.
/// </summary>
public static class AmbientesBanco
{
    /// <summary>Desenvolvimento. O unico que existia ate 06/11/2026.</summary>
    public static readonly AmbienteBanco Dev110 = new(
        Nome: "DEV110",
        Banco: "fugates_dev",
        PapelAcm: "fugates_acm_hml_app",
        PapelSd: "fugates_sd_app",
        PapelLeitura: "fugates_dev_read_app",
        PapelSapInbound: "fugates_sap_inbound_app",
        PapelMm: "fugates_mm_app",
        PapelDesktop: "fugates_dev_app");

    /// <summary>
    /// Qualidade. Banco proprio, PAPEIS proprios - e isso e deliberado: papel no PostgreSQL e do
    /// cluster, nao do banco. Reusar os papeis do DEV faria uma cadeia de conexao com o nome do
    /// banco trocado cruzar os ambientes em silencio; com papeis proprios, a credencial do QAS
    /// simplesmente nao abre o banco do DEV.
    /// </summary>
    public static readonly AmbienteBanco Qas120 = new(
        Nome: "QAS120",
        Banco: "fugates_qas",
        PapelAcm: "fugates_qas_acm_app",
        PapelSd: "fugates_qas_sd_app",
        PapelLeitura: "fugates_qas_read_app",
        PapelSapInbound: "fugates_qas_sap_inbound_app",
        PapelMm: "fugates_qas_mm_app",
        PapelDesktop: "fugates_qas_app");

    public static readonly AmbienteBanco[] Aprovados = [Dev110, Qas120];

    /// <summary>
    /// O ambiente deste par, entre os que o chamador ACEITA - e nulo quando o par nao e de nenhum
    /// deles, inclusive quando banco e papel existem mas pertencem a ambientes diferentes.
    ///
    /// Os ambientes aceitos vem do chamador de proposito: converter uma guarda compartilhada para
    /// consultar a lista inteira ampliaria, de uma vez, fluxos que ninguem pediu para ampliar.
    /// </summary>
    public static AmbienteBanco? Por(string? banco, string? papel, params AmbienteBanco[] aceitos)
    {
        var b = banco?.Trim();
        var p = papel?.Trim();
        if (string.IsNullOrEmpty(b) || string.IsNullOrEmpty(p)) return null;
        var lista = aceitos is { Length: > 0 } ? aceitos : Aprovados;
        return Array.Find(lista, a => string.Equals(a.Banco, b, StringComparison.Ordinal) && a.TemPapel(p));
    }

    public static bool EhPar(string? banco, string? papel, params AmbienteBanco[] aceitos)
        => Por(banco, papel, aceitos) is not null;

    /// <summary>O ambiente de um banco, sem olhar papel. So para mensagem e diagnostico.</summary>
    public static AmbienteBanco? PorBanco(string? banco)
        => banco is null ? null : Array.Find(Aprovados, a => string.Equals(a.Banco, banco.Trim(), StringComparison.Ordinal));
}
