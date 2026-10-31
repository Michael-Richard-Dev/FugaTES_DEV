using System.Buffers;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;

namespace FugaTES.Callback.Http;

public static partial class GuardianCallbackEndpoint
{
    public const int MaxCallbackBytes=1048576;
    public static void MapDual(IEndpointRouteBuilder endpoints,string route)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if(string.IsNullOrWhiteSpace(route))throw new ArgumentException("Callback route is required.",nameof(route));
        endpoints.MapPost(route,HandleDualAsync);
        ToledoCallbackProbeEndpoint.Map(endpoints,route);
    }
    public static Task HandleAsync(HttpContext context)=>HandleDualAsync(context);
    public static Task HandleEventoAsync(HttpContext context)=>HandleDualAsync(context);

    public static async Task HandleDualAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if(!context.Request.HasJsonContentType()){await WriteErrorAsync(context,415,"INVALID_CONTENT_TYPE");return;}
        if(context.Request.ContentLength>MaxCallbackBytes){await WriteErrorAsync(context,413,"CALLBACK_TOO_LARGE");return;}
        var original=context.Request.Body;
        try
        {
            using var buffer=new MemoryStream();
            var chunk=ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int read;
                while((read=await original.ReadAsync(chunk.AsMemory(0,8192),context.RequestAborted))>0)
                {
                    if(buffer.Length+read>MaxCallbackBytes){await WriteErrorAsync(context,413,"CALLBACK_TOO_LARGE");return;}
                    buffer.Write(chunk,0,read);
                }
            }
            finally{ArrayPool<byte>.Shared.Return(chunk,clearArray:true);}
            buffer.Position=0;
            using var document=await JsonDocument.ParseAsync(buffer,new(){MaxDepth=32},context.RequestAborted);
            var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || DuplicateProperties(root)){await WriteErrorAsync(context,400,"INVALID_CALLBACK_FORMAT");return;}
            var names=root.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Has(params string[] fields)=>fields.Any(names.Contains);
            var toledo=Has("SistemaBalanca","DataPesagem","TipoPesagem","IdPesagem","PesoTara","to_Pesagem","to_QualidPes");
            var guardianEvent=Has("ID","EstadoCodigo");
            var guardianMvp=Has("ProtocoloGuardian","Estado","VO_TICKET");
            var legacyOther=Has("Sequencial","Tara","PesoLiquido");
            if((toledo&&(guardianEvent||guardianMvp||legacyOther)) || (guardianEvent&&guardianMvp)
                || (!toledo&&!guardianEvent&&!guardianMvp))
            {await WriteErrorAsync(context,400,"INVALID_CALLBACK_FORMAT");return;}
            if(toledo){await HandleToledoAsync(context,root);return;}
            // Dispatch by the wire contract, independently of CorrelationProvider.
            buffer.Position=0;context.Request.Body=buffer;
            if(guardianEvent)await HandleEventoLegacyAsync(context);else await HandleLegacyAsync(context);
        }
        catch(JsonException){await WriteErrorAsync(context,400,"INVALID_JSON");}
        catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){throw;}
        catch{await WriteErrorAsync(context,500,"CALLBACK_FAILED");}
        finally{context.Request.Body=original;}
    }
    private static bool DuplicateProperties(JsonElement node)
    {
        if(node.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in node.EnumerateObject())if(!names.Add(p.Name)||DuplicateProperties(p.Value))return true;
        }
        else if(node.ValueKind==JsonValueKind.Array)return node.EnumerateArray().Any(DuplicateProperties);
        return false;
    }
    private static async Task HandleToledoAsync(HttpContext context,JsonElement root)
    {
        try
        {
            var dto=ToledoPesagemCallbackDto.Deserializar(root);
            if(context.RequestServices.GetService<IRegistroPesagemToledoStore>() is null)
            {await WriteErrorAsync(context,503,"TOLEDO_STORAGE_NOT_CONFIGURED");return;}
            var admission=context.RequestServices.GetService<IToledoCallbackAdmission>();
            if(admission is null){await WriteErrorAsync(context,503,"TOLEDO_AUTHENTICATION_NOT_CONFIGURED");return;}
            var actor=await admission.AuthorizeAsync(context,context.RequestAborted);
            if(!actor.Accepted){await WriteErrorAsync(context,actor.HttpStatus,actor.Code);return;}
            var receipt=await context.RequestServices.GetRequiredService<RegistrarPesagemToledoCasoDeUso>().ExecutarAsync(
                dto.Mapear() with{OrigemAutenticada=actor.SourceIdentity},context.RequestAborted);
            context.Response.StatusCode=200;
            await context.Response.WriteAsJsonAsync(new{status=receipt.SapIntegracaoId.HasValue?(receipt.Idempotente?"RECEIVED_SAP_PREVIOUSLY_ADMITTED":"RECEIVED_SAP_QUEUED"):"RECEIVED_PENDING_CORRELATION",receiptId=receipt.ReciboId,
                receivedAt=receipt.RecebidoEm,idempotent=receipt.Idempotente,persisted=true,
                downstream=receipt.SapIntegracaoId.HasValue?(receipt.Idempotente?"CHECK_SAP_JOURNAL_NO_REDISPATCH":"SAP_QUEUED_AFTER_COMMIT"):"BLOCKED_PENDING_LEGITIMATE_CORRELATION",sapIntegrationId=receipt.SapIntegracaoId},context.RequestAborted);
        }
        catch(ArgumentException error)
        {
            context.Response.StatusCode=400;
            await context.Response.WriteAsJsonAsync(new{error="INVALID_TOLEDO_CALLBACK",field=error.ParamName},CancellationToken.None);
        }
        catch(FugaTES.Aplicacao.Servicos.Recebimento.ToledoDeliveryPartnerException error)
        {
            context.Response.StatusCode=error.HttpStatus;
            var message=error.Code=="TOLEDO_DELIVERY_BP_NOT_FOUND"?"Código do parceiro de entrega não existe no SAP.":
                error.Code=="TOLEDO_DELIVERY_BP_NOT_SUPPLIER"?"Código do parceiro de entrega não está cadastrado como fornecedor no SAP.":
                error.Code=="TOLEDO_DELIVERY_BP_BLOCKED"?"Parceiro de entrega bloqueado no SAP.":"Não foi possível validar o parceiro de entrega no SAP. Recebimento não aceito.";
            await context.Response.WriteAsJsonAsync(new{error=error.Code,field="IdParceiroEntrega",indice=error.Index,codigo=error.BusinessPartner,message,persisted=false},CancellationToken.None);
        }
        catch(PesagemToledoConflitoException){await WriteErrorAsync(context,409,"TOLEDO_RECEIPT_CONFLICT");}
        catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){throw;}
        catch{await WriteErrorAsync(context,503,"TOLEDO_STORAGE_UNAVAILABLE");}
    }
}
