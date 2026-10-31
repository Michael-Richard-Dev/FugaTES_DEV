using FugaTES.Aplicacao.Modelos.Recebimento;
using System.Text.Json.Serialization;

namespace FugaTES.Callback.Http;

public sealed record GuardianCallbackHttpDto(
    string? ProtocoloGuardian,
    string? Sequencial,
    decimal? PesoBruto,
    decimal? Tara,
    decimal? PesoLiquido,
    string? Estado,
    [property: JsonPropertyName("VO_TICKET")] GuardianVoTicketHttpDto? VoTicket = null)
{
    public CallbackGuardianMvp Mapear()
    {
        if (string.IsNullOrWhiteSpace(ProtocoloGuardian) ||
            string.IsNullOrWhiteSpace(Sequencial) ||
            string.IsNullOrWhiteSpace(Estado) ||
            PesoBruto is null || Tara is null || PesoLiquido is null)
            throw new ArgumentException("Required callback field is absent.");
        if (PesoBruto < 0 || Tara < 0 || PesoLiquido < 0)
            throw new ArgumentException("Callback weights are invalid.");

        return new CallbackGuardianMvp(ProtocoloGuardian, Sequencial, PesoBruto.Value, Tara.Value, PesoLiquido.Value, Estado, VoTicket?.ProtocoloCloud);
    }
}

public sealed record GuardianVoTicketHttpDto(string? ProtocoloCloud);
