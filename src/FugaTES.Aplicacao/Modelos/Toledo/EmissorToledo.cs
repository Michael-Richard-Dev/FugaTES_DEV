namespace FugaTES.Aplicacao.Modelos.Toledo;

public sealed record EmissorToledo(
    string BusinessPartner,
    string RazaoSocial,
    int TipoEmissor,
    int TipoDocumento,
    string? Cnpj,
    string? InscricaoEstadual,
    string? Municipio,
    int? Uf,
    string? Cep,
    string? Email);

public sealed record EmissorToledoRegistrado(EmissorToledo Emissor);