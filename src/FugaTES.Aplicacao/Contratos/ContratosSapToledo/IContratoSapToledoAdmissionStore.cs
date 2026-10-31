using FugaTES.Aplicacao.Modelos.ContratosSapToledo;

namespace FugaTES.Aplicacao.Contratos.ContratosSapToledo;

public sealed record AcmInboundAdmission(Guid WindowId, DateTimeOffset NotBeforeUtc, DateTimeOffset UntilUtc)
{
    public string CorrelationPrefix => "r165ag:cpi:" + WindowId.ToString("N") + ":";
    public bool EhValida() => WindowId != Guid.Empty && NotBeforeUtc.Offset == TimeSpan.Zero &&
        UntilUtc.Offset == TimeSpan.Zero && UntilUtc > NotBeforeUtc && UntilUtc - NotBeforeUtc <= TimeSpan.FromDays(1);
    public bool EstaAberta(DateTimeOffset now) => EhValida() && now >= NotBeforeUtc && now < UntilUtc;
}

/// <summary>Same durable H4 store/transaction, with explicit first-admission semantics.</summary>
public interface IContratoSapToledoAdmissionStore : IContratoSapToledoVersaoStore
{
    Task<VersaoContratoSapToledo> RegistrarPrimeiroOuObterReentregaAsync(
        ContratoSapToledoSnapshot snapshot, string fingerprint, string payload,
        string correlationId, AcmInboundAdmission admission, CancellationToken cancellationToken);
}

public sealed class AcmInboundScopeConflictException : InvalidOperationException
{
    public AcmInboundScopeConflictException() : base("R165AG_ADMISSION_CONFLICT") { }
}
