using System.Net;
using FugaTES.Aplicacao.Servicos.Recebimento;
using System.Net.Http.Json;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using FugaTES.Aplicacao.Servicos.ManualRetry;

namespace FugaTES.Infraestrutura.ManualRetry;

public sealed record AcmRetryCommandBinding(Uri Endpoint,bool Homologated,string ApprovalReference,string OperatorSid,DateTimeOffset UntilUtc);

// Dedicated internal command client only. Never holds Guardian/SAP credentials or invokes business adapters.
public sealed class AuthenticatedAcmRetryCommandClient:IAcmManualRetryCommandClient,IComandoReenvioToledoSapClient,IDisposable
{
    readonly HttpClient http;readonly AcmRetryCommandBinding binding;readonly TimeProvider time;
    readonly Func<CancellationToken,Task>? prepare;
    bool disposed;
    public bool Configured=>!disposed&&binding.Homologated&&AcmManualRetryPolicy.Evidence(binding.ApprovalReference)&&time.GetUtcNow()<binding.UntilUtc;
    public AuthenticatedAcmRetryCommandClient(AcmRetryCommandBinding binding,string commandBearer,TimeProvider time,HttpMessageHandler? handler=null,Func<CancellationToken,Task>? prepare=null)
    {
        var u=binding.Endpoint;
        if(u.Scheme!="http"||u.Host!="127.0.0.1"||u.Port is <=1024 or 18761 or 18762||u.AbsolutePath!="/"||
            u.UserInfo.Length!=0||u.Query.Length!=0||u.Fragment.Length!=0||
            string.IsNullOrWhiteSpace(commandBearer)||commandBearer.Length is <32 or >4096||commandBearer.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)||c>127)||
            !binding.OperatorSid.StartsWith("S-1-5-21-",StringComparison.Ordinal))
            throw new InvalidOperationException("RETRY_SEPARATE_AUTHENTICATED_COMMAND_BINDING_REQUIRED");
        http=new HttpClient(handler??new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false}){Timeout=TimeSpan.FromSeconds(8),MaxResponseContentBufferSize=65536};
        http.DefaultRequestHeaders.Authorization=new("Bearer",commandBearer);this.binding=binding;this.time=time;this.prepare=prepare;
    }
    string Route(Guid id)=>"api/v1/acm/manual-retry/"+id.ToString("D");
    AcmRetryPreview Blocked(ContratoAcmLeitura row)=>new(row.IntegracaoId,row.NumeroContrato,row.Item,row.EstadoIntegracao,null,"",new(false,"COMMAND_NOT_HOMOLOGATED","Serviço de comando indisponível ou janela encerrada."),"");
    public async Task<AcmRetryPreview> PreviewAsync(ContratoAcmLeitura row,CancellationToken ct)
    {
        if(!Configured)return Blocked(row);
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);limit.CancelAfter(TimeSpan.FromSeconds(20));
        if(prepare is not null)await prepare(limit.Token);
        if(!Configured)return Blocked(row);
        using var response=await http.GetAsync(new Uri(binding.Endpoint,Route(row.IntegracaoId)+"/preview"),limit.Token);
        response.EnsureSuccessStatusCode();var value=await response.Content.ReadFromJsonAsync<AcmRetryPreview>(limit.Token)??throw new InvalidOperationException("RETRY_PREVIEW_EMPTY");
        // A denied preview deliberately withholds business data until the server finds a unit approval.
        // Its integration and authenticated operator must still match, even when contract/item are absent.
        if(value.IntegrationId!=row.IntegracaoId||value.OperatorSid!=binding.OperatorSid||value.Decision is null||
            (!string.IsNullOrEmpty(value.Contract)&&value.Contract!=row.NumeroContrato)||
            (value.Item is not null&&value.Item!=row.Item)||
            (value.Decision.Eligible&&(!AcmManualRetryPolicy.Evidence(value.Contract)||!AcmManualRetryPolicy.Evidence(value.Item)||
                value.Contract!=row.NumeroContrato||value.Item!=row.Item||!AcmManualRetryPolicy.Hash(value.SnapshotDigest))))
            throw new InvalidOperationException("RETRY_PREVIEW_IDENTITY_MISMATCH");return value;
    }
    public async Task<AcmRetryAdmission> SubmitAsync(AcmRetryRequest command,CancellationToken ct)
    {
        if(!Configured)return new("BLOCKED","COMMAND_NOT_HOMOLOGATED",command.CommandId,command.IntegrationId);
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);limit.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if(prepare is not null)await prepare(limit.Token);
            if(!Configured)return new("BLOCKED","COMMAND_NOT_HOMOLOGATED",command.CommandId,command.IntegrationId);
            // Exactly one command request. An unknown result is returned to the controller's existing latch.
            using var response=await http.PostAsJsonAsync(new Uri(binding.Endpoint,Route(command.IntegrationId)),command,limit.Token);
            if(response.StatusCode is not(HttpStatusCode.Accepted or HttpStatusCode.Conflict))
                return new("UNKNOWN","COMMAND_RESPONSE_UNVERIFIED_NO_RETRY",command.CommandId,command.IntegrationId);
            var receipt=await response.Content.ReadFromJsonAsync<AcmRetryAdmission>(limit.Token);
            if(receipt is null||receipt.CommandId!=command.CommandId||receipt.IntegrationId!=command.IntegrationId||
                (response.StatusCode==HttpStatusCode.Accepted)!=receipt.Accepted)
                return new("UNKNOWN","COMMAND_RECEIPT_UNVERIFIED_NO_RETRY",command.CommandId,command.IntegrationId);
            return receipt;
        }
        catch{return new("UNKNOWN","COMMAND_TRANSPORT_UNVERIFIED_NO_RETRY",command.CommandId,command.IntegrationId);}
    }
    public bool Configurado=>Configured;
    static string ToledoRoute(Guid id)=>"api/v1/toledo/sap/manual-retry/"+id.ToString("D");
    public async Task<PreviaReenvioToledoSap> PrepararAsync(Guid receipt,CancellationToken ct)
    {
        if(!Configured)throw new InvalidOperationException("TOLEDO_SAP_COMMAND_NOT_CONFIGURED");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        if(prepare is not null)await prepare(deadline.Token);
        if(!Configured)throw new InvalidOperationException("TOLEDO_SAP_COMMAND_WINDOW_CLOSED");
        using var response=await http.GetAsync(new Uri(binding.Endpoint,ToledoRoute(receipt)+"/preview"),deadline.Token);
        response.EnsureSuccessStatusCode();
        var preview=await response.Content.ReadFromJsonAsync<PreviaReenvioToledoSap>(deadline.Token)??throw new InvalidOperationException("TOLEDO_SAP_PREVIEW_EMPTY");
        if(preview.Snapshot.ReciboId!=receipt||preview.Decisao is null)throw new InvalidOperationException("TOLEDO_SAP_PREVIEW_IDENTITY_MISMATCH");
        return preview;
    }
    public async Task<ReciboComandoToledoSap> SolicitarAsync(ComandoReenvioToledoSap command,CancellationToken ct)
    {
        if(!Configured)return new(command.ComandoId,command.ReciboId,"BLOCKED");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            if(prepare is not null)await prepare(deadline.Token);
            if(!Configured)return new(command.ComandoId,command.ReciboId,"BLOCKED");
            using var response=await http.PostAsJsonAsync(new Uri(binding.Endpoint,ToledoRoute(command.ReciboId)),command,deadline.Token);
            if(response.StatusCode is not(HttpStatusCode.Accepted or HttpStatusCode.Conflict))return new(command.ComandoId,command.ReciboId,"UNKNOWN");
            var result=await response.Content.ReadFromJsonAsync<ReciboComandoToledoSap>(deadline.Token);
            return result is not null&&result.ComandoId==command.ComandoId&&result.ReciboId==command.ReciboId&&
                ((response.StatusCode==HttpStatusCode.Accepted&&result.Estado=="QUEUED")||(response.StatusCode==HttpStatusCode.Conflict&&result.Estado=="BLOCKED"))
                ?result:new(command.ComandoId,command.ReciboId,"UNKNOWN");
        }
        catch{return new(command.ComandoId,command.ReciboId,"UNKNOWN");}
    }
    public void Dispose(){disposed=true;http.Dispose();}
}
