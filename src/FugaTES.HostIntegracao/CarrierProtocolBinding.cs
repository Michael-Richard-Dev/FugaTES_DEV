using System.Globalization;
using System.Text.Json;
using FugaTES.Aplicacao.CasosDeUso.SAP;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Integracao.SAP.Autenticacao;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.Guardian;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace FugaTES.HostIntegracao;

public static class CarrierProtocolBinding
{
    public static SapCarrierDeltaContract? BuildSapContract(IConfiguration configuration)
    {
        var c=Freeze(configuration.GetSection("Carrier:Protocols:Sap"));
        if(!True(c,"Proven")||!True(c,"IncludesBpChanges")||!True(c,"IncludesNewCrm010Assignments")
            ||Missing(c,"ApprovalReference","Origin","DeltaTemplate","RowsPath","BusinessPartnerField","NextLinkPath","PageLimit"))return null;
        if(!Uri.TryCreate(c["Origin"],UriKind.Absolute,out var origin)||origin.Scheme!="https"||origin.UserInfo.Length!=0||origin.Fragment.Length!=0
            ||!int.TryParse(c["PageLimit"],out var limit)||limit is <1 or >1000)return null;
        var template=c["DeltaTemplate"]!;
        if(!template.Contains("{watermark}",StringComparison.Ordinal)||!template.Contains("{upperBound}",StringComparison.Ordinal)||!SafeRelative(template))return null;
        var initial=c["InitialTemplate"];
        if(initial is not null && (!initial.Contains("{upperBound}",StringComparison.Ordinal)||!SafeRelative(initial)))return null;
        return new(origin,c["ApprovalReference"]!,true,true,cursor=>
        {
            if(cursor.UpperBound is null)throw new CarrierGatewayException("SAP_DELTA_UPPER_BOUND_MISSING");
            if(cursor.PageToken is not null)return SameOrigin(origin,new Uri(origin,cursor.PageToken));
            string active=cursor.Watermark is null?initial??throw new CarrierGatewayException("SAP_INITIAL_DELTA_CONTRACT_NOT_PROVEN"):template;
            string value=active.Replace("{watermark}",Uri.EscapeDataString(cursor.Watermark?.ToString("O",CultureInfo.InvariantCulture)??""),StringComparison.Ordinal)
                .Replace("{upperBound}",Uri.EscapeDataString(cursor.UpperBound.Value.ToString("O",CultureInfo.InvariantCulture)),StringComparison.Ordinal);
            if(value.Contains('{')||value.Contains('}'))throw new CarrierGatewayException("SAP_DELTA_TEMPLATE_INVALID");
            return SameOrigin(origin,new Uri(origin,value));
        },json=>
        {
            var rows=Path(json,c["RowsPath"]!);
            if(rows.ValueKind!=JsonValueKind.Array)throw new CarrierGatewayException("SAP_DELTA_PAGE_INVALID");
            var bps=rows.EnumerateArray().Select(row=>Text(row,c["BusinessPartnerField"]!)).ToArray();
            string? next=null;
            if(TryPath(json,c["NextLinkPath"]!,out var n)&&n.ValueKind!=JsonValueKind.Null)
            {
                if(n.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(n.GetString()))throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");
                next=n.GetString();
            }
            return new(bps,next);
        },limit);
    }
    public static GuardianCarrierHmlContract? BuildGuardianContract(IConfiguration configuration)
    {
        var c=Freeze(configuration.GetSection("Carrier:Protocols:Guardian"));
        if(c["Environment"]!="HML"||!True(c,"ProfileVerified")||!True(c,"SignatureVerified")
            ||Missing(c,"ApprovalReference","LookupRoute","CreateRoute","UpdateRoute","ReadbackRoute","RowsPath","CodeField","NameField","LegalNameField",
                "WriteCodeField","WriteNameField","WriteLegalNameField","ResponseCodePath"))return null;
        if(new[]{"LookupRoute","CreateRoute","UpdateRoute","ReadbackRoute"}.Any(key=>!SafeRelative(c[key]!))
            ||!c["LookupRoute"]!.Contains("{bp}",StringComparison.Ordinal)||(!string.Equals(c["UpdateRoute"], "/api/v0/Guardian/Transportadora", StringComparison.Ordinal) && !c["UpdateRoute"]!.Contains("{code}", StringComparison.Ordinal))||!c["ReadbackRoute"]!.Contains("{code}",StringComparison.Ordinal))return null;
        var writeFields=new[]{c["WriteCodeField"]!,c["WriteNameField"]!,c["WriteLegalNameField"]!};
        if(writeFields.Distinct(StringComparer.Ordinal).Count()!=3)return null;
        // Guardian does not own the SAP BP identity; use only an approved FugaTES binding.
        if(string.Equals(c["BusinessPartnerField"],c["CodeField"],StringComparison.Ordinal))return null;
        var correlations=FreezeGuardianCorrelations(configuration);
        if(correlations.Count==0)return null;
        int? absence=null;
        if(c["AbsenceHttpStatus"] is {Length:>0} status)
        {
            if(!int.TryParse(status,out var code)||code!=404)return null;
            absence=code;
        }
        return new(true,true,c["ApprovalReference"]!,c["LookupRoute"]!,c["CreateRoute"]!,c["UpdateRoute"]!,c["ReadbackRoute"]!,absence,json=>
        {
            var rows=Path(json,c["RowsPath"]!);
            JsonElement row;
            if(rows.ValueKind==JsonValueKind.Array)
            {
                if(rows.GetArrayLength()>1)return new(CarrierLookupState.Ambiguous);
                if(rows.GetArrayLength()==0)return new(CarrierLookupState.Failed,ErrorCode:"GUARDIAN_CARRIER_EMPTY_RESULT_NOT_PROVEN_ABSENT");
                row=rows[0];
            }
            else if(rows.ValueKind==JsonValueKind.Object)row=rows;
            else throw new CarrierGatewayException("GUARDIAN_CARRIER_RESPONSE_INVALID");
            var wireCode=Text(row,c["CodeField"]!);
            var bound=correlations.Where(x=>string.Equals(x.GuardianCode,wireCode,StringComparison.Ordinal)).ToArray();
            if(bound.Length!=1)return new(CarrierLookupState.Failed,ErrorCode:"GUARDIAN_CARRIER_BP_CODE_BINDING_NOT_PROVEN");
            return new(CarrierLookupState.Unique,wireCode,new(bound[0].BusinessPartner,Text(row,c["NameField"]!),Text(row,c["LegalNameField"]!)));
        },attempt=>new Dictionary<string,string>
        {
            [c["WriteCodeField"]!]=attempt.GuardianCode??attempt.BusinessPartner,
            [c["WriteNameField"]!]=attempt.Payload.Name,
            [c["WriteLegalNameField"]!]=attempt.Payload.LegalName
        },json=>Text(json,c["ResponseCodePath"]!));
    }
    // Manual GET has its own proven contract; it does not enable CREATE/UPDATE.
    public static GuardianCarrierManualContract? BuildGuardianManualContract(IConfiguration configuration)
    {
        var c=Freeze(configuration.GetSection("Carrier:Protocols:Guardian"));
        if(c["Environment"]!="HML"||!True(c,"ProfileVerified")||!True(c,"SignatureVerified")
            ||Missing(c,"ApprovalReference","LookupRoute","CodeField","LegalNameField"))return null;
        if(!SafeRelative(c["LookupRoute"]!)||!c["LookupRoute"]!.Contains("{bp}",StringComparison.Ordinal))return null;
        if(string.Equals(c["CodeField"],c["LegalNameField"],StringComparison.Ordinal))return null;
        var scoped404=false;
        if(c["AbsenceHttpStatus"] is {Length:>0} status)
        {
            if(!int.TryParse(status,out var code)||code!=404)return null;
            scoped404=true;
        }
        return new(true,true,c["ApprovalReference"]!,c["LookupRoute"]!,c["CodeField"]!,c["LegalNameField"]!,scoped404);
    }
    public static void Register(IServiceCollection services,IConfiguration configuration,CarrierSyncPolicy policy)
    {
        services.TryAddSingleton<CarrierProtocolHttpClient>();
        services.AddSingleton<ISapCarrierDiscovery>(sp=>new CarrierSapDiscoveryAdapter(sp.GetRequiredService<CarrierProtocolHttpClient>(),sp.GetService<IAutenticacaoSap>(),
            sp.GetService<ConsultarTransportadoraSapCasoDeUso>(),BuildSapContract(configuration),sp.GetService<TimeProvider>()));
        services.AddSingleton<IGuardianCarrierGateway>(sp=>new GuardianCarrierSyncAdapter(sp.GetRequiredService<CarrierProtocolHttpClient>(),sp.GetService<GuardianHttpOptions>(),
            sp.GetService<GuardianTokenClient>(),BuildGuardianContract(configuration),attempt=>policy.PodeEscrever(attempt.BusinessPartner)));
    }
    public sealed record GuardianBpCorrelation(string BusinessPartner,string GuardianCode,string ApprovalReference);

    public static IReadOnlyList<GuardianBpCorrelation> FreezeGuardianCorrelations(IConfiguration configuration)
    {
        var bound=new List<GuardianBpCorrelation>();
        foreach(var entry in configuration.GetSection("Carrier:ManualReadOnly:Identities").GetChildren())
        {
            if(!True(entry,"Proven")||Missing(entry,"Code","ApprovalReference")||string.IsNullOrWhiteSpace(entry.Key))continue;
            bound.Add(new(entry.Key,entry["Code"]!,entry["ApprovalReference"]!));
        }
        if(bound.Select(x=>x.BusinessPartner).Distinct(StringComparer.Ordinal).Count()!=bound.Count
            ||bound.Select(x=>x.GuardianCode).Distinct(StringComparer.Ordinal).Count()!=bound.Count)return [];
        return bound;
    }
    private sealed class CarrierProtocolHttpClient():HttpClient(new HttpClientHandler{AllowAutoRedirect=false});
    private static IConfiguration Freeze(IConfigurationSection section)=>new ConfigurationBuilder()
        .AddInMemoryCollection(section.GetChildren().Select(c=>new KeyValuePair<string,string?>(c.Key,c.Value))).Build();
    private static bool True(IConfiguration c,string key)=>bool.TryParse(c[key],out var value)&&value;
    private static bool Missing(IConfiguration c,params string[] fields)=>fields.Any(key=>string.IsNullOrWhiteSpace(c[key]));
    private static bool SafeRelative(string value)=>!string.IsNullOrWhiteSpace(value)&&!value.StartsWith("//",StringComparison.Ordinal)&&!value.Contains('\\')&&!Uri.TryCreate(value,UriKind.Absolute,out _)&&!value.Contains('#');
    private static Uri SameOrigin(Uri origin,Uri uri)
    {
        if(uri.Scheme!=origin.Scheme||uri.Host!=origin.Host||uri.Port!=origin.Port||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw new CarrierGatewayException("SAP_DELTA_CURSOR_INVALID");
        return uri;
    }
    private static bool TryPath(JsonElement json,string path,out JsonElement result)
    {
        result=json;if(path=="$")return true;
        if(json.ValueKind==JsonValueKind.Object && json.TryGetProperty(path,out result))return true;
        result=json;
        foreach(var segment in path.Split('.'))
        {
            if(string.IsNullOrWhiteSpace(segment)||result.ValueKind!=JsonValueKind.Object||!result.TryGetProperty(segment,out result))return false;
        }
        return true;
    }
    private static JsonElement Path(JsonElement json,string path)=>TryPath(json,path,out var result)?result:throw new CarrierGatewayException("CARRIER_PROTOCOL_JSON_PATH_INVALID");
    private static string Text(JsonElement json,string path)
    {
        var result=Path(json,path);
        if(result.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(result.GetString()))throw new CarrierGatewayException("CARRIER_PROTOCOL_FIELD_INVALID");
        return result.GetString()!;
    }
}

