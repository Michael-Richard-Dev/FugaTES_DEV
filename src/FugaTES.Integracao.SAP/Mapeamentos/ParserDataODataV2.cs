using System.Text.RegularExpressions;

namespace FugaTES.Integracao.SAP.Mapeamentos;

internal static partial class ParserDataODataV2
{
    public static bool TentarParse(string? valor, out DateTimeOffset data)
    {
        data = default;

        if (string.IsNullOrWhiteSpace(valor))
        {
            return false;
        }

        var match = DataODataRegex().Match(valor);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var milliseconds))
        {
            return false;
        }

        try
        {
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if (!match.Groups[2].Success)
            {
                data = utc;
                return true;
            }

            var offsetText = match.Groups[2].Value;
            var sinal = offsetText[0] == '-' ? -1 : 1;
            var horas = int.Parse(offsetText.Substring(1, 2), System.Globalization.CultureInfo.InvariantCulture);
            var minutos = int.Parse(offsetText.Substring(3, 2), System.Globalization.CultureInfo.InvariantCulture);
            data = utc.ToOffset(new TimeSpan(sinal * horas, sinal * minutos, 0));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^/Date\((-?\d+)([+-]\d{4})?\)/$", RegexOptions.CultureInvariant)]
    private static partial Regex DataODataRegex();
}
