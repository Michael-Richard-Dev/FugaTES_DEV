namespace FugaTES.Aplicacao.Modelos.Toledo;

public sealed record TransportadoraToledo(
    string BusinessPartner,
    string? Nome,
    string Uf);

public sealed record TransportadoraToledoRegistrada(TransportadoraToledo Transportadora);