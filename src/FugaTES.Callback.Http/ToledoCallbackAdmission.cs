using System.Net;
using System.Security.Cryptography;
using System.Text;
using FugaTES.Callback.Http.ManualRetry;

namespace FugaTES.Callback.Http;

public sealed record ToledoCallbackActor(bool Accepted,string SourceIdentity,int HttpStatus,string Code);
public interface IToledoCallbackAdmission
{Task<ToledoCallbackActor> AuthorizeAsync(HttpContext context,CancellationToken ct);}

public sealed record ToledoCallbackAdmissionOptions
{
    public string SourceIdentity{get;init;}="";
    public string HeaderName{get;init;}="";
    public string SecretFile{get;init;}="";
    public IPAddress[] AllowedPeers{get;init;}=[];
    public bool PrivateLoopbackProxyApproved{get;init;}
    // Explicit operator-approved DEV network boundary; never the default mode.
    public bool PrivateDevNetworkOnly{get;init;}
    public bool Configured=>SourceIdentity.Length is >0 and <=160&&!SourceIdentity.Any(char.IsControl)&&AllowedPeers.Length>0&&
        (PrivateDevNetworkOnly?PrivateLoopbackProxyApproved:
        HeaderName.Length is >0 and <=64&&HeaderName.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-')&&
        Path.IsPathFullyQualified(SecretFile)&&!SecretFile.Any(char.IsControl));
    public static ToledoCallbackAdmissionOptions Parse(IConfiguration config)
    {
        var s=config.GetSection("ToledoInbound:Authentication");
        var raw=s.GetSection("AllowedRemoteAddresses").GetChildren().Select(p=>p.Value).ToArray();
        var peers=new List<IPAddress>();foreach(var p in raw){if(!IPAddress.TryParse(p,out var ip))return new();peers.Add(ip.IsIPv4MappedToIPv6?ip.MapToIPv4():ip);}
        bool True(string key)=>bool.TryParse(s[key],out var flag)&&flag;
        var mode=s["Mode"]??"SharedSecret";
        if(mode is not ("SharedSecret" or "PrivateDevNetworkOnly"))return new();
        var privateProxy=s["Environment"] is "DEV" or "HML"&&True("PrivateTransportVerified")&&True("SourceVerified")&&
            !string.IsNullOrWhiteSpace(s["ApprovalReference"])&&True("PrivateLoopbackProxyApproved")&&peers.Count==1&&peers[0].Equals(IPAddress.Loopback);
        var networkOnly=mode=="PrivateDevNetworkOnly";
        if(networkOnly&&(s["Environment"]!="DEV"||s["PrivateListenerPort"]!="18086"||!privateProxy))return new();
        return new(){SourceIdentity=s["SourceIdentity"]??"",HeaderName=s["HeaderName"]??"",SecretFile=s["SecretFile"]??"",AllowedPeers=peers.ToArray(),
            PrivateLoopbackProxyApproved=privateProxy,PrivateDevNetworkOnly=networkOnly};
    }
}

// Same existing restricted-file reader used by the command host. No secret is generated,
// copied, stored in configuration, exported, logged or read until this admitted request.
public sealed class ProtectedToledoCallbackAdmission(ToledoCallbackAdmissionOptions options,Func<string,byte[]>? readProtected=null):IToledoCallbackAdmission
{
    public Task<ToledoCallbackActor> AuthorizeAsync(HttpContext context,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        static ToledoCallbackActor Deny(int status,string code)=>new(false,"",status,code);
        if(!options.Configured)return Task.FromResult(Deny(503,"TOLEDO_AUTHENTICATION_NOT_CONFIGURED"));
        var peer=context.Connection.RemoteIpAddress;
        if(peer?.IsIPv4MappedToIPv6==true)peer=peer.MapToIPv4();
        var privateProxy=options.PrivateLoopbackProxyApproved&&peer is not null&&IPAddress.IsLoopback(peer)&&
            context.Connection.LocalIpAddress is { } local&&IPAddress.IsLoopback(local);
        // X-Forwarded-For/Proto and a generic loopback peer never prove sender identity.
        if(peer is null||!options.AllowedPeers.Contains(peer)||(!context.Request.IsHttps&&!privateProxy))
            return Task.FromResult(Deny(403,"TOLEDO_SOURCE_NOT_ALLOWED"));
        if(options.PrivateDevNetworkOnly)
        {
            if(!privateProxy||context.Connection.LocalPort!=18086)
                return Task.FromResult(Deny(403,"TOLEDO_SOURCE_NOT_ALLOWED"));
            // This proves only admission through the configured private ingress, not
            // Guardian sender identity. Deployment must retain Nginx allowlist/deny-all.
            return Task.FromResult(new ToledoCallbackActor(true,options.SourceIdentity,200,"TOLEDO_PRIVATE_NETWORK_ADMITTED"));
        }
        var header=context.Request.Headers[options.HeaderName];
        if(header.Count!=1||header[0] is not {Length:>=32 and <=1024} value||value.Any(char.IsControl))
            return Task.FromResult(Deny(401,"TOLEDO_AUTHENTICATION_REQUIRED"));
        try
        {
            var bytes=(readProtected??(path=>PrivateCommandFile.Read(path,1026)))(options.SecretFile);
            try
            {
                var secret=new UTF8Encoding(false,true).GetString(bytes).TrimEnd('\r','\n');
                if(secret.Length is <32 or >1024||secret.Any(char.IsControl))return Task.FromResult(Deny(503,"TOLEDO_AUTHENTICATION_NOT_CONFIGURED"));
                if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)),SHA256.HashData(Encoding.UTF8.GetBytes(value))))
                    return Task.FromResult(Deny(401,"TOLEDO_AUTHENTICATION_REQUIRED"));
                return Task.FromResult(new ToledoCallbackActor(true,options.SourceIdentity,200,"TOLEDO_SOURCE_AUTHENTICATED"));
            }
            finally{CryptographicOperations.ZeroMemory(bytes);}
        }
        catch{return Task.FromResult(Deny(503,"TOLEDO_AUTHENTICATION_NOT_CONFIGURED"));}
    }
}
