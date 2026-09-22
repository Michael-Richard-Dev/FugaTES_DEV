namespace FugaTES.Aplicacao.Modelos.Toledo;

public sealed record PreCadastroToledo(
    string NumeroContratoBase,
    TipoMovimentoPreCadastro TipoMovimento,
    string? CodigoFluxo,
    string? CodigoItem,
    string? CodigoTransportadora,
    string? DataAgendamento,
    string? NumeroReferencia);

public enum TipoMovimentoPreCadastro
{
    Carga,
    Descarga
}
