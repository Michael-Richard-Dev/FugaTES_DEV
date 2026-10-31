using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Callback.Http;

public sealed record GuardianEventoHttpDto(JsonElement ID, JsonElement Sequencial,
    JsonElement EstadoCodigo, JsonElement PesoBruto, JsonElement Tara, JsonElement PesoLiquido)
{
    private static readonly Regex PesoPattern = new(@"^[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex PesoLiquidoPattern = new(@"^[0-9]+\.[0-9]{2}$", RegexOptions.CultureInvariant);

    public EventoGuardianRecebimento Mapear()
    {
        if (ID.ValueKind != JsonValueKind.Number || !ID.TryGetInt64(out var id) ||
            Sequencial.ValueKind != JsonValueKind.Number || !Sequencial.TryGetInt64(out var sequencial) ||
            EstadoCodigo.ValueKind != JsonValueKind.Number || !EstadoCodigo.TryGetInt32(out var estado) ||
            estado is not (1 or 3 or 5))
            throw new ArgumentException("Required Guardian event fields are invalid.");

        var bruto = ParseWeight(PesoBruto, "PesoBruto", false);
        var tara = ParseWeight(Tara, "Tara", false);
        var liquido = ParseWeight(PesoLiquido, "PesoLiquido", true);
        if (estado == 5 && (bruto is null || tara is null || liquido is null))
            throw new ArgumentException("All final weighing fields are required.");
        return new(id, sequencial, estado, bruto, tara, liquido);
    }

    private static decimal? ParseWeight(JsonElement wire, string name, bool requireTwoPlaces)
    {
        if (wire.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (wire.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a decimal string.");
        var value = wire.GetString();
        if (value is null || !(requireTwoPlaces ? PesoLiquidoPattern : PesoPattern).IsMatch(value) ||
            !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{name} format is invalid.");

        var separator = value.IndexOf('.');
        var wireScale = separator < 0 ? 0 : value.Length - separator - 1;
        var wireCoefficient = BigInteger.Parse(value.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        var bits = decimal.GetBits(parsed);
        var parsedCoefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) +
            ((BigInteger)(uint)bits[2] << 64);
        var parsedScale = (bits[3] >> 16) & 0x7F;
        if (wireCoefficient * BigInteger.Pow(10, parsedScale) !=
            parsedCoefficient * BigInteger.Pow(10, wireScale))
            throw new ArgumentException($"{name} cannot be represented exactly.");
        return parsed;
    }
}
