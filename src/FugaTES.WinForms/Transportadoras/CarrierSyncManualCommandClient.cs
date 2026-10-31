using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Transportadoras;

namespace FugaTES.WinForms.Transportadoras;

public sealed record CarrierManualResult(Guid RequestId, bool Accepted, string Code, bool Indeterminate = false)
{
    public static CarrierManualResult Unknown(Guid id) => new(id, false, "CARRIER_REQUEST_RESULT_UNKNOWN", true);
}
public interface ICarrierSyncManualCommandClient
{
    Task<CarrierManualResult> RequestAsync(Guid requestId, string businessPartner, CancellationToken ct);
}
public sealed class CarrierSyncManualCommandClient : ICarrierSyncManualCommandClient
{
    public const string PipeName = "FugaTES.CarrierSync.Manual.v1";
    public async Task<CarrierManualResult> RequestAsync(Guid requestId, string businessPartner, CancellationToken ct)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("CARRIER_REQUEST_INVALID");
        CarrierPersistencePolicy.ValidateBp(businessPartner);
        var sent = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Impersonation);
        try
        {
            await pipe.ConnectAsync(3000, timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            sent = true;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { RequestId = requestId, BusinessPartner = businessPartner }).AsMemory(), timeout.Token);
            var line = await ReadBoundedLineAsync(reader, timeout.Token);
            var response = JsonSerializer.Deserialize<Response>(line);
            if (response is null || response.Code is not ("CARRIER_REQUEST_ACCEPTED" or "CARRIER_REQUEST_NOT_AUTHORIZED" or "CARRIER_REQUEST_NOT_READY")) return CarrierManualResult.Unknown(requestId);
            return new(requestId, response.Accepted && response.Code == "CARRIER_REQUEST_ACCEPTED", response.Code);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
        {
            return sent ? CarrierManualResult.Unknown(requestId) : new(requestId, false, "CARRIER_REQUEST_NOT_READY");
        }
    }
    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[1];
        while (result.Length <= 4096)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), ct) == 0) throw new IOException("CARRIER_PIPE_CLOSED");
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(buffer[0]);
        }
        throw new IOException("CARRIER_PIPE_RESPONSE_INVALID");
    }
    private sealed record Response(bool Accepted, string Code);
}
