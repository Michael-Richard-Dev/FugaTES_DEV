using FugaTES.Callback.Http;
namespace FugaTES.Testes;
public sealed class ToledoSapReadOnlyModeTests
{
 [Fact] public void Normal_host_does_not_enter_the_administrative_SAP_check()=>Assert.False(ToledoSapAuthenticationReadOnly.Selected([]));
 [Theory][InlineData("--toledo-sap-auth-read-only")][InlineData("--toledo-sap-auth-read-only-forged")]
 public void Incomplete_or_forged_mode_cannot_boot_an_alternate_host(string mode)=>Assert.Throws<InvalidOperationException>(()=>ToledoSapAuthenticationReadOnly.Selected([mode]));
 [Fact] public void Extra_arguments_or_relative_secret_directory_are_rejected()
 {Assert.Throws<InvalidOperationException>(()=>ToledoSapAuthenticationReadOnly.Selected([ToledoSapAuthenticationReadOnly.Flag,"relative"]));Assert.Throws<InvalidOperationException>(()=>ToledoSapAuthenticationReadOnly.Selected([ToledoSapAuthenticationReadOnly.Flag,Path.GetTempPath(),"--urls=http://0.0.0.0"]));}
 [Fact] public void The_only_accepted_check_has_an_explicit_local_provider_directory()=>Assert.True(ToledoSapAuthenticationReadOnly.Selected([ToledoSapAuthenticationReadOnly.Flag,Path.GetTempPath()]));
}
