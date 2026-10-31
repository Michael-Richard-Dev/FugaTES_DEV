using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Integracao.SAP.Mapeamentos;
namespace FugaTES.Integracao.SAP.BusinessPartner;

public static class CarrierManualODataDateParser
{
    public static DateTimeOffset? Parse(string? value)
    {
        if(value is null)return null;
        var m=Regex.Match(value,@"^/Date\((-?\d+)([+-])(\d{2})(\d{2})\)/$",RegexOptions.CultureInvariant);
        if(m.Success)
        {
            int hours=int.Parse(m.Groups[3].Value),minutes=int.Parse(m.Groups[4].Value);
            if(minutes>59||hours>14||hours==14&&minutes!=0)return null;
        }
        return ParserDataODataV2.TentarParse(value,out var date)?date:null;
    }
}
public sealed class CarrierManualSapReadOnlyAdapter(ClienteHttpParceiroNegocioSap client):ISapCarrierManualReadOnly
{
    public async Task<CarrierManualSap> ConsultarBpAsync(string bp,CancellationToken ct)
    {
        CarrierPersistencePolicy.ValidateBp(bp);
        // Existing BP GET expands to_BusinessPartnerRole (A_BusinessPartnerRole) in the same response.
        var response=await client.ObterAsync(bp,ct,solicitarJson:true).ConfigureAwait(false);
        if(response.FalhaConfiguracao is not null||response.StatusCode!=HttpStatusCode.OK||response.Conteudo is null)throw new CarrierGatewayException("SAP_MANUAL_GET_FAILED");
        using var json=JsonDocument.Parse(response.Conteudo);
        var data=json.RootElement.GetProperty("d");
        var identity=data.GetProperty("BusinessPartner").GetString();
        if(identity!=bp)throw new CarrierGatewayException("SAP_MANUAL_IDENTITY_MISMATCH");
        var blocked=data.GetProperty("BusinessPartnerIsBlocked");
        bool isBlocked=blocked.ValueKind is JsonValueKind.True or JsonValueKind.False?blocked.GetBoolean():
            bool.TryParse(blocked.GetString(),out var flag)?flag:throw new CarrierGatewayException("SAP_MANUAL_BLOCK_FLAG_INVALID");
        var nav=data.GetProperty("to_BusinessPartnerRole");var roles=new List<CarrierManualRole>();
        foreach(var row in nav.GetProperty("results").EnumerateArray())
        {
            if(row.TryGetProperty("BusinessPartner",out var rowBp)&&rowBp.GetString()!=bp)throw new CarrierGatewayException("SAP_MANUAL_ROLE_IDENTITY_MISMATCH");
            static string? Text(JsonElement row,string key)=>row.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
            roles.Add(new(Text(row,"BusinessPartnerRole")??"",CarrierManualODataDateParser.Parse(Text(row,"ValidFrom")),CarrierManualODataDateParser.Parse(Text(row,"ValidTo"))));
        }
        var fields=new Dictionary<string,string?>(StringComparer.Ordinal);
        foreach(var key in new[]{"BusinessPartner","BusinessPartnerFullName","BusinessPartnerName"})
            if(data.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String)fields[key]=v.GetString();
        return new(bp,fields,roles,isBlocked,!nav.TryGetProperty("__next",out _));
    }
}
