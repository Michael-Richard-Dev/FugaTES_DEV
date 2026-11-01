using FugaTES.Aplicacao.Modelos.Sd;

namespace FugaTES.HostIntegracao;

/// <summary>
/// Recorte do motor de protocolos SD, lido da configuracao do servico.
///
/// Tudo aqui tem padrao seguro: se a configuracao vier incompleta, o que falta assume o valor
/// mais restritivo, nunca o mais permissivo. Configuracao invalida impede o servico de subir -
/// um motor que cria protocolo real com recorte errado e pior do que um motor parado.
/// </summary>
public sealed class SdProtocoloOptions
{
    public const string Secao = "SdProtocolo";

    /// <summary>Chave que liga o motor. Ausente ou falsa, o servico sobe sem fazer nada.</summary>
    public bool Enabled { get; set; }

    /// <summary>Intervalo entre varreduras. Decisao do operador em 01/11/2026: 3 minutos.</summary>
    public TimeSpan Intervalo { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Data minima de criacao da ordem. Padrao: o corte combinado com o operador. O acervo
    /// anterior nao vira protocolo sozinho.
    /// </summary>
    public DateOnly CorteCriacao { get; set; } = OrdensVendaSdFiltro.CorteAutomatico;

    /// <summary>Teto de protocolos por varredura. O que sobrar vai na volta seguinte.</summary>
    public int ProtocolosPorVarredura { get; set; } = 10;

    /// <summary>Quantas ORDENS a varredura traz do SAP por volta.</summary>
    public int OrdensPorVarredura { get; set; } = 200;

    /// <summary>Centros do fluxo. Vazio assume os dois permitidos.</summary>
    public IList<string> Centros { get; } = [];

    /// <summary>Tipos de documento de venda. Vazio traz todos, como na tela.</summary>
    public IList<string> TiposDocumento { get; } = [];

    /// <summary>Para onde o Guardian devolve a pesagem. Sem ela nao se cria protocolo.</summary>
    public string? UrlParaRetorno { get; set; }

    public OrdensVendaSdFiltro MontarFiltro() => new(
        OrdensVendaSdFiltro.OrganizacaoFixa,
        Centros.Count > 0 ? [.. Centros] : OrdensVendaSdFiltro.CentrosPermitidos,
        [.. TiposDocumento],
        OrdensPorVarredura)
    {
        DataCriacaoMinima = CorteCriacao
    };

    /// <summary>
    /// Por que a configuracao nao serve. Devolve nulo quando serve - o texto vai para o log, e
    /// por isso nao carrega nada alem de nomes de campo.
    /// </summary>
    public string? Reprovar()
    {
        if (Intervalo < TimeSpan.FromSeconds(30) || Intervalo > TimeSpan.FromHours(1))
            return "SD_MOTOR_INTERVALO_INVALIDO";
        if (ProtocolosPorVarredura is < 1 or > 100) return "SD_MOTOR_TETO_INVALIDO";
        if (string.IsNullOrWhiteSpace(UrlParaRetorno)
            || !Uri.TryCreate(UrlParaRetorno, UriKind.Absolute, out var retorno)
            || retorno.Scheme is not ("http" or "https"))
            return "SD_MOTOR_URL_RETORNO_INVALIDA";
        // O corte e a unica defesa contra o motor varrer o acervo antigo. Data muito no passado
        // anularia essa defesa sem ninguem perceber.
        if (CorteCriacao < new DateOnly(2026, 1, 1)) return "SD_MOTOR_CORTE_ANTIGO_DEMAIS";
        if (!MontarFiltro().EhValido()) return "SD_MOTOR_FILTRO_INVALIDO";
        return null;
    }
}
