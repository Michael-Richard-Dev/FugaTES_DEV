using System.Text.Json;
using System.Text.Json.Serialization;
namespace FugaTES.AcmSoapLookup.R33;
public static class Program
{
    public static bool TryParseArguments(string[] args,out LookupRequestR33? request,out string? profilePath)
    {
        request=null;profilePath=null;
        if(args.Length!=9||args[0]!="--execute-read")return false;
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        for(int i=1;i<args.Length;i+=2)
            if(args[i] is not ("--code" or "--center" or "--reference-version" or "--profile")||!values.TryAdd(args[i],args[i+1]))return false;
        if(values.Count!=4||!values.TryGetValue("--code",out var code)||!values.TryGetValue("--center",out var center)||
            !values.TryGetValue("--reference-version",out var version)||version!="2"||!values.TryGetValue("--profile",out var path)||!Path.IsPathFullyQualified(path))return false;
        var scope=new LookupRequestR33(code,center,2);if(!LookupRunnerR33.ScopeValid(scope))return false;
        request=scope;profilePath=path;return true;
    }
    public static async Task<int> Main(string[] args)
    {
        LookupResultR33 result;
        try
        {
            // Explicit invocation and local scope checked BEFORE profile I/O or HttpClient creation.
            if(!TryParseArguments(args,out var request,out var path)){Console.WriteLine("BLOCKED:EXPLICIT_READ_SCOPE_REQUIRED");return 2;}
            var profile=JsonSerializer.Deserialize<LookupProfileR33>(File.ReadAllText(path!),new JsonSerializerOptions{PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow});
            if(profile is null||!LookupRunnerR33.ProfileValid(profile,DateTimeOffset.UtcNow)){Console.WriteLine("BLOCKED:SOAP_READ_PROFILE_NOT_APPROVED");return 2;}
            using var handler=new HttpClientHandler{AllowAutoRedirect=false};
            result=await LookupRunnerR33.RunAsync(request!,profile,handler).ConfigureAwait(false);
        }
        catch{result=new("BLOCKED","SOAP_LOCAL_CONFIGURATION_INVALID");}
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.State=="LOOKUP_CONFIRMED"?0:result.State=="BLOCKED"?2:3;
    }
}
