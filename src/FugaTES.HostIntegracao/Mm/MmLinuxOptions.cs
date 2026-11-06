using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Modelos.Mm;
namespace FugaTES.HostIntegracao.Mm;
public sealed record MmLinuxOptions
{
 public bool Enabled{get;init;}
 public bool MasterdataValidationEnabled{get;init;}
 public bool RetryCommandsEnabled{get;init;}
 public int IntervalSeconds{get;init;}=180;
 public DateOnly CreationCutoff{get;init;}=MmCollectionPolicy.CreationCutoff;
 public string Host{get;init;}="127.0.0.1";
 public int Port{get;init;}=5433;
 public string SapOrigin{get;init;}=AmbientesSap.Dev110.Origem;
 public string SapClient{get;init;}=AmbientesSap.Dev110.SapClient;
 public AmbienteSap AmbienteSap=>AmbientesSap.Por(SapOrigin,SapClient)??throw new InvalidOperationException("MM_SAP_ENVIRONMENT_REJECTED");
 public AmbienteBanco AmbienteBanco=>AmbientesBanco.PorNome(AmbienteSap.Nome)!;
 public string Database{get;init;}=AmbientesBanco.Dev110.Banco;
 public string Role{get;init;}=AmbientesBanco.Dev110.PapelMm;
 public string StateDirectory{get;init;}="/var/lib/fugates-mm";
 public string CallbackUrl{get;init;}="";
 public string ActivationReference{get;init;}="";
 public string WindowsCutoverReference{get;init;}="";
 public bool WindowsCollectorDisabled{get;init;}
 public void Validate()
 {
  if(Host!="127.0.0.1"||Port!=5433||AmbientesBanco.Por(Database,Role,AmbienteBanco)!=AmbienteBanco||Role!=AmbienteBanco.PapelMm)throw new InvalidOperationException("MM_DATABASE_SCOPE_INVALID");
  if(IntervalSeconds!=180||CreationCutoff!=MmCollectionPolicy.CreationCutoff)throw new InvalidOperationException("MM_COLLECTION_POLICY_INVALID");
  if(StateDirectory!=(AmbienteBanco==AmbientesBanco.Dev110?"/var/lib/fugates-mm":"/var/lib/fugates-mm-qas"))throw new InvalidOperationException("MM_STATE_DIRECTORY_INVALID");
  if(RetryCommandsEnabled&&!MasterdataValidationEnabled)throw new InvalidOperationException("MM_RETRY_MASTERDATA_REQUIRED");
  if(!Enabled)return;
  if(!WindowsCollectorDisabled||string.IsNullOrWhiteSpace(ActivationReference)||string.IsNullOrWhiteSpace(WindowsCutoverReference))throw new InvalidOperationException("MM_CUTOVER_AUTHORIZATION_REQUIRED");
  if(!Uri.TryCreate(CallbackUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)throw new InvalidOperationException("MM_CALLBACK_INVALID");
 }
}
