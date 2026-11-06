using System.Net;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
namespace FugaTES.Testes;
public sealed class ToledoIngressPairTests
{
 // If runtime trusts the DEV literal instead of the configured ingress, the QAS case is admitted.
 static ToledoCallbackAdmissionOptions Bound(string environment,int port)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
 { ["Environment"]=environment,["PrivateListenerPort"]=port.ToString(System.Globalization.CultureInfo.InvariantCulture) }).Build().Get<ToledoCallbackAdmissionOptions>()!
 with {SourceIdentity="SYNTHETIC_APPROVED_INGRESS",AllowedPeers=[IPAddress.Loopback],PrivateLoopbackProxyApproved=true,PrivateDevNetworkOnly=true};
 static Dictionary<string,string?> Configuration(string environment,string port)=>new()
 {
  ["ToledoInbound:Authentication:Mode"]="PrivateDevNetworkOnly",["ToledoInbound:Authentication:Environment"]=environment,
  ["ToledoInbound:Authentication:PrivateListenerPort"]=port,["ToledoInbound:Authentication:SourceIdentity"]="SYNTHETIC_"+environment,
  ["ToledoInbound:Authentication:AllowedRemoteAddresses:0"]="127.0.0.1",["ToledoInbound:Authentication:PrivateLoopbackProxyApproved"]="true",
  ["ToledoInbound:Authentication:PrivateTransportVerified"]="true",["ToledoInbound:Authentication:SourceVerified"]="true",["ToledoInbound:Authentication:ApprovalReference"]="SYNTHETIC_APPROVAL"
 };
 static ToledoCallbackAdmissionOptions Parse(Dictionary<string,string?> config)=>ToledoCallbackAdmissionOptions.Parse(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
 [Theory][InlineData("DEV",18086)][InlineData("QAS120",18087)]
 public async Task Approved_complete_pair_passes_only_on_its_real_listener(string environment,int port)
 {var o=Parse(Configuration(environment,port.ToString()));Assert.True(o.Configured);var actor=await new ProtectedToledoCallbackAdmission(o,_=>throw new InvalidOperationException("NO_SECRET_READ")).AuthorizeAsync(Socket(port),default);Assert.True(actor.Accepted);Assert.Equal(200,actor.HttpStatus);Assert.Equal("SYNTHETIC_"+environment,actor.SourceIdentity);}
 [Theory][InlineData("DEV","18087")][InlineData("QAS120","18086")][InlineData("HML","18086")][InlineData("DEV110","18086")][InlineData("PRD","18087")][InlineData("DEV","18088")][InlineData("QAS120","18087 ")][InlineData("QAS120","")]
 public async Task Crossed_or_unapproved_configured_tuple_is_not_configured(string environment,string port)
 {var o=Parse(Configuration(environment,port));Assert.False(o.Configured);var actor=await new ProtectedToledoCallbackAdmission(o).AuthorizeAsync(Socket(18086),default);Assert.False(actor.Accepted);Assert.Equal(503,actor.HttpStatus);}
 [Theory][InlineData("DEV",18086)][InlineData("QAS120",18087)]
 public async Task Approval_and_real_loopback_checks_remain_required_for_each_pair(string environment,int port)
 {
  foreach(var key in new[]{"PrivateTransportVerified","SourceVerified","PrivateLoopbackProxyApproved","ApprovalReference"})
  {var c=Configuration(environment,port.ToString());c["ToledoInbound:Authentication:"+key]=key=="ApprovalReference"?"":"false";Assert.False(Parse(c).Configured);}
  var good=Parse(Configuration(environment,port.ToString()));
  foreach(var part in new[]{"peer","local"})
  {var c=Socket(port);if(part=="peer")c.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.1");else c.Connection.LocalIpAddress=IPAddress.Any;c.Request.Headers["X-Forwarded-For"]="127.0.0.1";c.Request.Headers["X-Forwarded-Proto"]="https";Assert.False((await new ProtectedToledoCallbackAdmission(good).AuthorizeAsync(c,default)).Accepted);}
 }
 [Fact] public async Task QAS_does_not_turn_shared_secret_mode_into_a_keyless_HTTP_bypass()
 {var c=Configuration("QAS120","18087");c["ToledoInbound:Authentication:Mode"]="SharedSecret";c["ToledoInbound:Authentication:HeaderName"]="X-Synthetic-Key";c["ToledoInbound:Authentication:SecretFile"]=Path.GetFullPath("SYNTHETIC_NO_FILE");var o=Parse(c);Assert.True(o.Configured);var context=Socket(18087);context.Request.Headers["X-Synthetic-Key"]=new string('K',64);var auth=new ProtectedToledoCallbackAdmission(o,_=>System.Text.Encoding.UTF8.GetBytes(new string('K',64)));Assert.False((await auth.AuthorizeAsync(context,default)).Accepted);context.Request.Scheme="https";Assert.True((await auth.AuthorizeAsync(context,default)).Accepted);}
 static DefaultHttpContext Socket(int port)
 {var c=new DefaultHttpContext();c.Request.Scheme="http";c.Connection.RemoteIpAddress=IPAddress.Loopback;c.Connection.LocalIpAddress=IPAddress.Loopback;c.Connection.LocalPort=port;return c;}
 [Theory][InlineData("DEV",18086,18087)][InlineData("QAS120",18087,18086)]
 public async Task Correct_configured_pair_cannot_authorize_the_other_real_socket(string environment,int configuredPort,int realPort)
 {
  var context=Socket(realPort);context.Request.Headers["X-Forwarded-Port"]=configuredPort.ToString();context.Request.Headers["X-Forwarded-Proto"]="https";
  var actor=await new ProtectedToledoCallbackAdmission(Bound(environment,configuredPort),_=>throw new InvalidOperationException("NO_SECRET_READ_IN_NETWORK_MODE")).AuthorizeAsync(context,default);
  Assert.False(actor.Accepted);Assert.Equal(403,actor.HttpStatus);Assert.Equal("TOLEDO_SOURCE_NOT_ALLOWED",actor.Code);
 }
}
