using System.Net.Http.Headers;
using System.Text.Json;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.Integracao.Guardian;
/// <summary>Contrato SD adaptado ao MM. Usa o provider/token existente, não repete POST.</summary>
public sealed class GuardianProtocoloMmClient(HttpClient http,Func<CancellationToken,Task<string>> token,TimeSpan timeout):IEnviarProtocoloMmGateway
{
    public const string Endpoint="https://guardian.cloudprix.com.br/api/v1/Guardian/ProtocoloPreCadastro";
    public async Task<ResultadoProtocoloMm> EnviarAsync(byte[] payload,CancellationToken ct)
    {
        if(timeout<=TimeSpan.Zero||timeout>TimeSpan.FromSeconds(60))throw new InvalidOperationException("MM_TIMEOUT_INVALIDO");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout);
        var bearer=await token(deadline.Token);
        using var request=new HttpRequestMessage(HttpMethod.Post,Endpoint){Content=new ByteArrayContent(payload)};
        request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bearer);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        var status=(int)response.StatusCode;
        if(status is >=400 and <500 and not 408)
        {
            byte[]? body=null;
            if(response.Content.Headers.ContentLength is null or <=8192)
            {
                await using var errorInput=await response.Content.ReadAsStreamAsync(deadline.Token);
                using var errorMemory=new MemoryStream();var errorBuffer=new byte[1024];int read;
                while((read=await errorInput.ReadAsync(errorBuffer,deadline.Token))>0)
                {if(errorMemory.Length+read>8192){errorMemory.SetLength(0);break;}errorMemory.Write(errorBuffer,0,read);}
                if(errorMemory.Length>0)body=errorMemory.ToArray();
            }
            var code=GuardianRestR57HttpException.ReadSafeErrorCode(body,bearer);
            var safe=$"Guardian recusou a requisição (HTTP {status})."+(code is null?"":" Código: "+code+".");
            return new(EstadoProtocoloMm.Recusado,status,null,safe.Length<=400?safe:safe[..400]);
        }
        if(!response.IsSuccessStatusCode)return new(EstadoProtocoloMm.Indeterminado,status,null,"Resposta externa sem conclusão comprovada; não reenviar automaticamente.");
        try
        {
            await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);
            using var memory=new MemoryStream();var buffer=new byte[4096];int read;
            while((read=await input.ReadAsync(buffer,deadline.Token))>0)
            {if(memory.Length+read>65536)return Unknown(status);memory.Write(buffer,0,read);}
            using var document=JsonDocument.Parse(memory.ToArray());
            var ids=new HashSet<long>();var invalid=false;
            Visit(document.RootElement,0);
            return !invalid&&ids.Count==1?new(EstadoProtocoloMm.Criado,status,ids.Single(),"Protocolo Guardian confirmado."):Unknown(status);
            void Visit(JsonElement node,int depth,bool onlyAudit=false)
            {
                if(depth>4){invalid=true;return;}
                if(node.ValueKind==JsonValueKind.Number){if(onlyAudit)return;if(node.TryGetInt64(out var id)&&id>0)ids.Add(id);else invalid=true;return;}
                if(node.ValueKind!=JsonValueKind.Object){invalid=true;return;}
                foreach(var property in node.EnumerateObject())
                {
                    if(property.Name.Equals("error",StringComparison.OrdinalIgnoreCase)&&property.Value.ValueKind is not(JsonValueKind.Null or JsonValueKind.False))invalid=true;
                    if(property.Name.Equals("success",StringComparison.OrdinalIgnoreCase)||property.Name.Equals("sucesso",StringComparison.OrdinalIgnoreCase))
                        if(property.Value.ValueKind!=JsonValueKind.True)invalid=true;
                    if(!onlyAudit&&new[]{"id","protocolo","idProtocolo","numeroProtocolo"}.Contains(property.Name))
                    {if(property.Value.ValueKind==JsonValueKind.Number&&property.Value.TryGetInt64(out var id)&&id>0)ids.Add(id);
                     else if(property.Value.ValueKind==JsonValueKind.String&&long.TryParse(property.Value.GetString(),out var textId)&&textId>0)ids.Add(textId);else invalid=true;}
                    if(new[]{"dadosProtocoloPreCadastro","dados","data","result"}.Contains(property.Name))Visit(property.Value,depth+1,onlyAudit);
                    if(property.Name=="audit")Visit(property.Value,depth+1,true);
                }
            }
        }
        catch(JsonException){return Unknown(status);}
    }
    private static ResultadoProtocoloMm Unknown(int status)=>new(EstadoProtocoloMm.Indeterminado,status,null,"Resposta sem protocolo inequívoco; exige reconciliação.");
}
