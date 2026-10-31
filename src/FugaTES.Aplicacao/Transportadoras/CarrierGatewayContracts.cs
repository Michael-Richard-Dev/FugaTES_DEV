namespace FugaTES.Aplicacao.Transportadoras;

public sealed record CarrierPayload(string BusinessPartner, string Name, string LegalName);
public sealed record CarrierCursor(DateTimeOffset? Watermark, DateTimeOffset? UpperBound, string? PageToken)
{
    public string[] VisitedPages { get; init; } = [];
}
public sealed record CarrierPage(IReadOnlyList<CarrierPayload> Carriers, CarrierCursor NextCursor, bool End);
public enum CarrierLookupState { Absent, Unique, Ambiguous, Failed }
public sealed record CarrierLookup(CarrierLookupState State, string? GuardianCode = null, CarrierPayload? Payload = null, string? ErrorCode = null);
public sealed record CarrierWriteAttempt(Guid Id, string BusinessPartner, string? GuardianCode, string Action, CarrierPayload Payload);
public sealed record CarrierWriteResult(bool Accepted, bool Indeterminate, string? GuardianCode = null, string? ErrorCode = null)
{
    public int? HttpStatusCode { get; init; }
}
public interface ISapCarrierDiscovery { Task<CarrierPage> LerPaginaAsync(CarrierCursor cursor, CancellationToken ct); }
public interface IGuardianCarrierGateway
{
    Task<CarrierLookup> ConsultarAsync(string bp, CancellationToken ct);
    Task<CarrierWriteResult> GravarAsync(CarrierWriteAttempt tentativa, CancellationToken ct);
    Task<CarrierLookup> LerConfirmacaoAsync(string codigo, CancellationToken ct);
}
public sealed class CarrierGatewayException(string code) : Exception(code) { public string Code { get; } = code; }

