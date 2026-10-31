namespace FugaTES.Aplicacao.Modelos.Guardian;

public sealed record ContratoGuardianSet26(
    string Codigo,
    string Descricao,
    decimal ValorContrato,
    string Estado,
    decimal ValorProximidadeFimContrato,
    string TipoLancamento)
{
    public string? CodigoEmissor { get; init; }
    public string? CodigoItem { get; init; }
    // Null preserves associations; an explicitly empty collection requests removal.
    public IReadOnlyList<VeiculoContratoGuardian>? Veiculos { get; init; }
}

public sealed record VeiculoContratoGuardian(string Codigo, string PlacaVeiculo);