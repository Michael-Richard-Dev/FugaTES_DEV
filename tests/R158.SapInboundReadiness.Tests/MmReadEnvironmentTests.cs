using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.Mm;
namespace FugaTES.Testes;
public sealed class MmReadEnvironmentTests
{
 [Theory][InlineData(false)][InlineData(true)]
 public async Task Read_source_never_attaches_other_environment_cache_to_its_database(bool qas)
 {
  var expected=qas?AmbientesSap.Qas120:AmbientesSap.Dev110;var other=qas?AmbientesSap.Dev110:AmbientesSap.Qas120;
  var directory=Path.Combine(Path.GetTempPath(),"FugaTES-mm-read-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
  try
  {
   new MmFileSnapshotStore(directory).Write(new(){Origin=expected.Origem,Client=expected.SapClient,RuntimeMode="VM",Host="vm-fugatesbio"});
   var view=await new MmSnapshotReadSource(directory,expected).ReadAsync(default);
   Assert.Equal(expected.Origem,view.Snapshot.Origin);Assert.Equal(expected.SapClient,view.Snapshot.Client);
   await Assert.ThrowsAsync<InvalidOperationException>(()=>new MmSnapshotReadSource(directory,other).ReadAsync(default));
  }
  finally{Directory.Delete(directory,true);}
 }
}
