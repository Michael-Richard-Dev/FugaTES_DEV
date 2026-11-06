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
 public string Database{get;init;}="fugates_dev";
 public string Role{get;init;}="fugates_mm_app";
 public string StateDirectory{get;init;}="/var/lib/fugates-mm";
 public string CallbackUrl{get;init;}="";
 public string ActivationReference{get;init;}="";
 public string WindowsCutoverReference{get;init;}="";
 public bool WindowsCollectorDisabled{get;init;}
 public void Validate()
 {
  if(Host!="127.0.0.1"||Port!=5433||Database!="fugates_dev"||Role!="fugates_mm_app")throw new InvalidOperationException("MM_DATABASE_SCOPE_INVALID");
  if(IntervalSeconds!=180||CreationCutoff!=MmCollectionPolicy.CreationCutoff)throw new InvalidOperationException("MM_COLLECTION_POLICY_INVALID");
  if(StateDirectory!="/var/lib/fugates-mm")throw new InvalidOperationException("MM_STATE_DIRECTORY_INVALID");
  if(RetryCommandsEnabled&&!MasterdataValidationEnabled)throw new InvalidOperationException("MM_RETRY_MASTERDATA_REQUIRED");
  if(!Enabled)return;
  if(!WindowsCollectorDisabled||string.IsNullOrWhiteSpace(ActivationReference)||string.IsNullOrWhiteSpace(WindowsCutoverReference))throw new InvalidOperationException("MM_CUTOVER_AUTHORIZATION_REQUIRED");
  if(!Uri.TryCreate(CallbackUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)throw new InvalidOperationException("MM_CALLBACK_INVALID");
 }
}
