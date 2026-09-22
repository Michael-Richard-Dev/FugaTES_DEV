namespace FugaTES.Aplicacao.Modelos.SAP;

public sealed record ParceiroNegocioConsultado(
    string BusinessPartner,
    string? Nome,
    IReadOnlyCollection<DocumentoFiscalNormalizado> DocumentosFiscais,
    EnderecoParceiroNegocio? EnderecoSelecionado,
    IReadOnlyCollection<string> Telefones,
    IReadOnlyCollection<string> Emails,
    SituacaoBloqueioSap BloqueioSap,
    CategoriaParceiroNegocio CategoriaValidada)
{
    public int? BusinessPartnerCategory { get; init; }

    public string? NomeCompletoParceiroNegocio { get; init; }
}
