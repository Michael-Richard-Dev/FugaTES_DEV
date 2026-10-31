using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace FugaTES.HostIntegracao;
public sealed class CarrierSyncManualCommandServer(CarrierManualRequestHandler handler,ILogger<CarrierSyncManualCommandServer> logger):BackgroundService
{
    public const string PipeName="FugaTES.CarrierSync.Manual.v1";
    sealed record Request(Guid RequestId,string BusinessPartner);
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static string? ReadCurrentSid() {using var identity=WindowsIdentity.GetCurrent();return identity.User?.Value;}
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("CARRIER_WINDOWS_REQUIRED");
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader=new StreamReader(pipe,new UTF8Encoding(false,true),false,512,true);
                using var writer=new StreamWriter(pipe,new UTF8Encoding(false),512,true){AutoFlush=true};
                var buffer=new char[1];var line=new StringBuilder();
                while(line.Length<=4096)
                {
                    if(await reader.ReadAsync(buffer.AsMemory(),timeout.Token)==0)break;
                    if(buffer[0]=='\n')break;
                    line.Append(buffer[0]);
                }
                if(line.Length>4096)throw new InvalidDataException();
                var request=JsonSerializer.Deserialize<Request>(line.ToString(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
                string? sid=null;
                pipe.RunAsClient(()=>{if(OperatingSystem.IsWindows())sid=ReadCurrentSid();});
                var response=request is null||sid is null?new CarrierManualResponse(false,"CARRIER_REQUEST_NOT_AUTHORIZED"):
                    await handler.HandleAsync(request.RequestId,request.BusinessPartner,sid,timeout.Token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(),timeout.Token);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch{logger.LogWarning("CARRIER_MANUAL_CHANNEL_FAILED");}
        }
    }
}