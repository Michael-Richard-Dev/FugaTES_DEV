using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FugaTES.Aplicacao.Modelos.Recebimento;

public static class CallbackGuardianMvpFingerprint
{
    public static string Calcular(CallbackGuardianMvp callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var ticket = Exigir(callback.Sequencial, nameof(callback.Sequencial));
        var estado = Exigir(callback.Estado, nameof(callback.Estado)).ToUpperInvariant();
        var canonical = string.Join('|', ticket, estado,
            callback.PesoBruto.ToString("G29", CultureInfo.InvariantCulture),
            callback.Tara.ToString("G29", CultureInfo.InvariantCulture),
            callback.PesoLiquido.ToString("G29", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Exigir(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Required callback fingerprint field is absent.", name);
        return value.Trim();
    }
}
