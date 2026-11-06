namespace FugaTES.Aplicacao.Modelos.Sd;

/// <summary>
/// SD: pesagem devolvida pelo Guardian apos a balanca, antes de qualquer decisao sobre o SAP.
///
/// Guarda o que identifica, o que pesa e o que correlaciona - nada interpretado. O documento
/// original vai junto em <see cref="PayloadJson"/>: quando o formato mudar, a evidencia do que
/// chegou continua sendo o proprio documento, nao esta projecao.
/// </summary>
public sealed record DevolutivaPesagemSd(
    // Chave do evento no Guardian. E por ela que um reenvio do mesmo POST e reconhecido.
    string IdPesagem,
    string Centro,
    // "S" para venda (saida). "E" e recebimento e nao pertence a este fluxo.
    string TipoPesagem,
    DateTime DataPesagem,
    decimal PesoBruto,
    decimal PesoTara,
    decimal PesoLiquido,
    // Correlacao: ordem e linha, ja normalizadas, como gravadas no protocolo.
    string? OrdemVenda,
    string? LinhaOrdem,
    string? Placa,
    string? NomeMotorista,
    string PayloadJson)
{
    /// <summary>
    /// Contrato do ACM, quando a pesagem traz um. E o que diz A QUEM a pesagem pertence.
    ///
    /// O Guardian manda a MESMA finalizacao de pesagem para as duas rotas, a do ACM e a nossa, e
    /// ate 03/11/2026 nos guardavamos as duas. A pesagem 0020344 virou uma linha de venda em
    /// branco: sem ordem, sem linha, sem nada a fazer com ela - porque era do ACM.
    ///
    /// Preenchido (e diferente de zero) significa: nao e do fluxo de venda. Vazio, ausente ou
    /// zerado significa que e nossa. A regra e do operador, e o campo ja vem no documento.
    /// </summary>
    public string? ContratoAcm { get; init; }

    public bool EhDoAcm => ContratoAcm is not null;

    /// <summary>Chave natural da linha de ordem, no mesmo formato do registro de protocolos.</summary>
    public string? ChaveNatural => OrdemVenda is null || LinhaOrdem is null ? null : OrdemVenda + "/" + LinhaOrdem;

    public bool TemCorrelacao => ChaveNatural is not null;
}

/// <summary>Por que uma devolutiva nao pode ser aceita. Nada e descartado em silencio.</summary>
public sealed class DevolutivaSdInvalidaException(string codigo, string detalhe, string? idPesagem = null)
    : Exception(codigo + ": " + detalhe)
{
    public string Codigo { get; } = codigo;
    public string Detalhe { get; } = detalhe;

    /// <summary>
    /// A pesagem, quando ja tinha sido lida antes da recusa. Nula quando o documento sequer
    /// chegou a dar o IdPesagem - um JSON invalido, por exemplo.
    /// </summary>
    public string? IdPesagem { get; } = idPesagem;
}

/// <summary>
/// A recusa que significa "este documento nao e do fluxo de venda", e nao "este documento esta
/// errado". Hoje ha uma so: o tipo de pesagem e entrada.
/// </summary>
public static class DevolutivaSdCodigos
{
    public const string TipoNaoEhSaida = "SD_DEVOLUTIVA_TIPO_NAO_E_SAIDA";
}
