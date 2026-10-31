using FugaTES.HostIntegracao;
namespace FugaTES.Testes;
public sealed class ToledoSystemdCredentialScopeTests
{
 const string Directory="/run/credentials/fugates-toledo-callback.service";
 const UnixFileMode File440=UnixFileMode.UserRead|UnixFileMode.GroupRead;
 const UnixFileMode Dir550=UnixFileMode.UserRead|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute;
 const UnixFileMode Parent755=Dir550|UnixFileMode.UserWrite|UnixFileMode.OtherRead|UnixFileMode.OtherExecute;
 static bool Scope(string name="carrier-hml-sap-password",string? path=null,string? runtime=null,UnixFileMode mode=Dir550,UnixFileMode parent=Parent755,bool links=false)
  =>CarrierSystemdCredentials.IsApprovedToledoRuntimeDirectory(name,path??Directory,runtime??Directory,mode,parent,links);
 [Fact] public void Observed440_is_rejected_by_legacy_guard_without_runtime_proof()
 {Assert.Throws<CarrierServiceStartupException>(()=>CarrierSystemdCredentials.ValidateMode(File440));}
 [Theory][InlineData("carrier-hml-sap-user")][InlineData("carrier-hml-sap-password")]
 public void Systemd_read_only_group_mode_requires_the_exact_Toledo_runtime_proof(string name)
 {Assert.True(Scope(name));CarrierSystemdCredentials.ValidateMode(File440,Scope(name));}
 [Theory][InlineData("carrier-hml-postgres-password")][InlineData("carrier-hml-guardian-password")][InlineData("../carrier-hml-sap-password")]
 public void Other_credential_ids_cannot_receive_the_Toledo_exception(string name)=>Assert.False(Scope(name));
 [Theory][InlineData("/tmp/fugates-toledo-callback.service")][InlineData("/etc/fugates/toledo/credentials")][InlineData("/run/credentials/other.service")][InlineData("/run/credentials/x/../fugates-toledo-callback.service")][InlineData("/run/credentials/fugates-toledo-callback.service/")]
 public void Arbitrary_paths_are_not_systemd_provenance(string path)=>Assert.False(Scope(path:path,runtime:path));
 [Fact] public void Missing_or_mismatching_environment_reference_is_rejected()
 {Assert.False(CarrierSystemdCredentials.IsApprovedToledoRuntimeDirectory("carrier-hml-sap-user",Directory,null,Dir550,Parent755,false));Assert.False(Scope(runtime:"/run/credentials/other.service"));}
 [Theory][InlineData(UnixFileMode.UserWrite)][InlineData(UnixFileMode.GroupWrite)][InlineData(UnixFileMode.OtherRead)][InlineData(UnixFileMode.OtherWrite)][InlineData(UnixFileMode.OtherExecute)]
 public void Credential_directory_with_write_or_public_access_is_rejected(UnixFileMode extra)=>Assert.False(Scope(mode:Dir550|extra));
 [Theory][InlineData(UnixFileMode.GroupWrite)][InlineData(UnixFileMode.OtherWrite)]
 public void Publicly_replaceable_parent_is_rejected(UnixFileMode extra)=>Assert.False(Scope(parent:Parent755|extra));
 [Fact] public void Symlinked_context_cannot_prove_systemd_origin()=>Assert.False(Scope(links:true));
 [Fact] public void Unproven_group_read_never_becomes_a_global_exception()=>Assert.Throws<CarrierServiceStartupException>(()=>CarrierSystemdCredentials.ValidateMode(File440,false));
 [Theory][InlineData(UnixFileMode.UserWrite)][InlineData(UnixFileMode.GroupWrite)][InlineData(UnixFileMode.OtherRead)][InlineData(UnixFileMode.OtherWrite)][InlineData(UnixFileMode.UserExecute)][InlineData(UnixFileMode.GroupExecute)]
 public void Runtime_proof_does_not_allow_other_credential_permission_bits(UnixFileMode extra)=>Assert.Throws<CarrierServiceStartupException>(()=>CarrierSystemdCredentials.ValidateMode(File440|extra,true));
 [Fact] public void Legacy_owner_read_only_policy_stays_compatible()=>CarrierSystemdCredentials.ValidateMode(UnixFileMode.UserRead,false);
}
