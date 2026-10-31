using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Aplicacao.CasosDeUso.Recebimento;

public sealed class ProcessarEventoGuardianCasoDeUso(IRegistroEventoGuardianStore store)
{
    public async Task<StatusRegistroEventoGuardian> ExecutarAsync(
        EventoGuardianRecebimento evento, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evento);
        if (evento.Id <= 0 || evento.Sequencial <= 0)
            throw new ArgumentException("GUARDIAN_EVENT_IDENTITY_INVALID", nameof(evento));
        if (evento.EstadoCodigo is not (1 or 3 or 5))
            throw new ArgumentException("Unknown Guardian event state.", nameof(evento));
        if (evento.EstadoCodigo == 5 &&
            (evento.PesoBruto is null || evento.Tara is null || evento.PesoLiquido is null))
            throw new ArgumentException("Final weighing requires three callback weights.", nameof(evento));
        if (evento.EstadoCodigo == 5)
            GuardianFinalWeighingPolicy.RequireFinal(evento.PesoBruto!.Value, evento.Tara!.Value, evento.PesoLiquido!.Value);

        var fingerprint = CalcularFingerprint(evento);
        var result = await store.RegistrarAsync(evento, fingerprint, cancellationToken);
        if (result == StatusRegistroEventoGuardian.Conflito)
            throw new InvalidOperationException("Guardian callback conflicts with persisted event.");
        return result;
    }

    public static string CalcularFingerprint(EventoGuardianRecebimento evento)
    {
        var canonical = string.Join('|',
            evento.Id.ToString(CultureInfo.InvariantCulture),
            evento.Sequencial.ToString(CultureInfo.InvariantCulture),
            evento.EstadoCodigo.ToString(CultureInfo.InvariantCulture),
            evento.PesoBruto?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
            evento.Tara?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty,
            evento.PesoLiquido?.ToString("G29", CultureInfo.InvariantCulture) ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
