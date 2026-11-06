using FugaTES.Aplicacao.Configuracao;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.HostIntegracao.Mm;
public static class MmRetryPolicyFile
{
 public const string PolicyPath="/home/michael/.fugates-mm/config/retry-policy.json";
 public const string RuntimeDirectory="/run/credentials/fugates-mm-purchase-orders.service";
 public const string RuntimePolicyPath=RuntimeDirectory+"/mm-retry-policy";
 public const string DatabasePath="/home/michael/.fugates-mm/config/database.credential.json";
 public static string RuntimeDirectoryFor(AmbienteBanco ambiente)=>ambiente==AmbientesBanco.Dev110?RuntimeDirectory:ambiente==AmbientesBanco.Qas120?"/run/credentials/fugates-mm-qas-purchase-orders.service":throw new InvalidOperationException("MM_RETRY_ENVIRONMENT_REJECTED");
 public static string PolicyPathFor(AmbienteBanco ambiente)=>ambiente==AmbientesBanco.Dev110?PolicyPath:ambiente==AmbientesBanco.Qas120?"/home/michael/.fugates-mm-qas/config/retry-policy.json":throw new InvalidOperationException("MM_RETRY_ENVIRONMENT_REJECTED");
 public static string DatabasePathFor(AmbienteBanco ambiente)=>Path.Combine(Path.GetDirectoryName(PolicyPathFor(ambiente))!,"database.credential.json");
 [DllImport("libc")]static extern uint geteuid();
 public static MmRetryOptions ReadPolicy(string path,AmbienteBanco? ambiente=null)
 {
  ambiente??=AmbientesBanco.Dev110;
  if(path!=PolicyPathFor(ambiente)&&path!=RuntimeDirectoryFor(ambiente)+"/mm-retry-policy")throw new InvalidOperationException("MM_RETRY_POLICY_REFERENCE_REJECTED");
  var policy=JsonSerializer.Deserialize<MmRetryOptions>(Read(path,65536,ambiente),new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new InvalidOperationException("MM_RETRY_POLICY_INVALID");policy.Validate();return policy;
 }
 public static byte[] ReadDatabase(AmbienteBanco? ambiente=null)=>Read(DatabasePathFor(ambiente??AmbientesBanco.Dev110),16384,ambiente??AmbientesBanco.Dev110);
 public static bool ApprovedRuntimeScope(string path,string? credentialsDirectory,UnixFileMode file,UnixFileMode directory,bool links,params AmbienteBanco[] aceitos)
  =>!links&&(aceitos.Length==0?new[]{AmbientesBanco.Dev110}:aceitos).Any(a=>path==RuntimeDirectoryFor(a)+"/mm-retry-policy"&&credentialsDirectory==RuntimeDirectoryFor(a))
   &&(file==UnixFileMode.UserRead||file==(UnixFileMode.UserRead|UnixFileMode.GroupRead))
   &&(directory==(UnixFileMode.UserRead|UnixFileMode.UserExecute)||directory==(UnixFileMode.UserRead|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute));
 static byte[] Read(string path,int limit,AmbienteBanco ambiente)
 {
  if(!OperatingSystem.IsLinux()||Environment.MachineName!="vm-fugatesbio"||!Path.IsPathFullyQualified(path))throw new InvalidOperationException("MM_RETRY_PRIVATE_HOST_REQUIRED");
  var file=new FileInfo(path);if(!file.Exists||file.Length is <=0||file.Length>limit)throw new InvalidOperationException("MM_RETRY_PRIVATE_FILE_INVALID");
  var directory=RuntimeDirectoryFor(ambiente);var runtime=path==directory+"/mm-retry-policy";
  if(runtime&&!ApprovedRuntimeScope(path,Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY"),File.GetUnixFileMode(path),File.GetUnixFileMode(directory),file.LinkTarget is not null||(file.Attributes&FileAttributes.ReparsePoint)!=0,ambiente))throw new InvalidOperationException("MM_RETRY_RUNTIME_SCOPE_REJECTED");
  var uid=geteuid();
  for(FileSystemInfo? n=file;n is not null;n=n is FileInfo f?f.Directory:((DirectoryInfo)n).Parent)
  {
   if(n.LinkTarget is not null||(n.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("MM_RETRY_PRIVATE_LINK_REJECTED");
   var mode=File.GetUnixFileMode(n.FullName);var forbidden=UnixFileMode.GroupWrite|UnixFileMode.OtherWrite;
   if(n==file)forbidden|=(runtime?0:UnixFileMode.GroupRead)|UnixFileMode.OtherRead|UnixFileMode.GroupExecute|UnixFileMode.OtherExecute|UnixFileMode.UserExecute|UnixFileMode.SetUser|UnixFileMode.SetGroup|UnixFileMode.StickyBit;
   if((mode&forbidden)!=0)throw new InvalidOperationException("MM_RETRY_PRIVATE_MODE_REJECTED");
   using var p=new Process{StartInfo=new("/usr/bin/stat"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true}};
   p.StartInfo.ArgumentList.Add("--format=%u");p.StartInfo.ArgumentList.Add("--");p.StartInfo.ArgumentList.Add(n.FullName);p.Start();var value=p.StandardOutput.ReadToEnd().Trim();
   if(!p.WaitForExit(5000)||p.ExitCode!=0||!uint.TryParse(value,out var owner)||owner!=0&&owner!=uid)throw new InvalidOperationException("MM_RETRY_PRIVATE_OWNER_REJECTED");
  }
  return File.ReadAllBytes(path);
 }
}
