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
    /// <summary>Chave natural da linha de ordem, no mesmo formato do registro de protocolos.</summary>
    public string? ChaveNatural => OrdemVenda is null || LinhaOrdem is null ? null : OrdemVenda + "/" + LinhaOrdem;

    public bool TemCorrelacao => ChaveNatural is not null;
}

/// <summary>Por que uma devolutiva nao pode ser aceita. Nada e descartado em silencio.</summary>
public sealed class DevolutivaSdInvalidaException(string codigo, string detalhe)
    : Exception(codigo + ": " + detalhe)
{
    public string Codigo { get; } = codigo;
    public string Detalhe { get; } = detalhe;
}
