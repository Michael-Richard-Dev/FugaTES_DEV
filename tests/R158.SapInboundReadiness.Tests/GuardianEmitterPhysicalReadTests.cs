using System.Text.Json;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.WinForms.Transportadoras;
namespace FugaTES.Testes;
public sealed class GuardianEmitterPhysicalReadTests
{
    [Fact] public async Task Optional_existing_HML_emitter_GET_uses_approved_provider_without_business_writes()
    {
        var output=Environment.GetEnvironmentVariable("FUGATES_EMITTER_READ_RESULT");if(string.IsNullOrEmpty(output))return;
        var profile=CarrierDesktopProfile.Load(@"C:\FugaTES_DEV110\FugaTES.carrier.json");
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(25)};
        var options=new GuardianHttpOptions{BaseUrl=new Uri("https://guardian.cloudprix.com.br"),Timeout=TimeSpan.FromSeconds(15)};
        var tokens=new GuardianTokenClient(http,options,new CarrierServiceGuardianCredentials(profile));
        var result=await new GuardianEmitterReadAdapter(http,options,tokens,133289).ConsultarAsync("20000169",default);
        File.WriteAllText(output,JsonSerializer.Serialize(new{atUtc=DateTimeOffset.UtcNow,operation="GET_EMISSOR",candidate="20000169",signatureId=133289,
            result.Confirmado,result.Http,result.Motivo,confirmedCode=result.Confirmado?result.Codigo:null,sequence=result.Confirmado?result.Sequencial:(long?)null,
            existingProvider=true,businessPosts=0,tokenNeverExported=true,payloadNeverExported=true},new JsonSerializerOptions{WriteIndented=true}));
        Assert.True(result.Confirmado,result.Motivo);
    }
}
