using FugaTES.HostIntegracao.Mm;
namespace FugaTES.Testes;
public sealed class MmRetryPolicyFileScopeTests
{
 const string Directory="/run/credentials/fugates-mm-purchase-orders.service";
 [Theory]
 [InlineData(256,320,true)][InlineData(288,360,true)]
 [InlineData(384,320,false)][InlineData(292,360,false)][InlineData(304,360,false)][InlineData(288,493,false)]
 public void Only_read_only_systemd_MM_modes_are_accepted(int file,int directory,bool expected)
 {
  Assert.Equal(expected,MmRetryPolicyFile.ApprovedRuntimeScope(Directory+"/mm-retry-policy",Directory,(UnixFileMode)file,(UnixFileMode)directory,false));
 }
 [Theory][InlineData("/run/credentials/other.service/mm-retry-policy",Directory,false)][InlineData(Directory+"/mm-retry-policy","/run/credentials/other.service",false)][InlineData(Directory+"/mm-retry-policy",Directory,true)]
 public void Foreign_directory_environment_or_link_cannot_admit_group_read(string path,string env,bool link)
 {
  Assert.False(MmRetryPolicyFile.ApprovedRuntimeScope(path,env,UnixFileMode.UserRead|UnixFileMode.GroupRead,UnixFileMode.UserRead|UnixFileMode.UserExecute,link));
 }
}
