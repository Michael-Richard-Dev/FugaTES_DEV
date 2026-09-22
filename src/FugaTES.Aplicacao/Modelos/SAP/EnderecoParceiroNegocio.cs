namespace FugaTES.Aplicacao.Modelos.SAP;

public sealed record EnderecoParceiroNegocio(
    string AddressId,
    string? Logradouro,
    string? Cidade,
    string? Regiao,
    string? CodigoPostal,
    string? Pais);
