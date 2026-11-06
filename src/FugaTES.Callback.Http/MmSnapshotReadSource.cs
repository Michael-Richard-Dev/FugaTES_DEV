using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Infraestrutura.Mm;
namespace FugaTES.Callback.Http;
public interface IMmSnapshotReadSource
{Task<MmRemoteReadback> ReadAsync(CancellationToken ct);}
/// <summary>Private observation cache, never a replacement for the PostgreSQL business journal.</summary>
public sealed class MmSnapshotReadSource(string directory,AmbienteSap? ambiente=null):IMmSnapshotReadSource
{
 public Task<MmRemoteReadback> ReadAsync(CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();
  var value=MmSnapshotDocumentGuard.ForRead(new MmFileSnapshotStore(directory,readOnly:true,ambiente:ambiente??AmbientesSap.Dev110).Read());
  if(value.RuntimeMode!="VM"||value.Host!="vm-fugatesbio"||value.IntervalSeconds!=180||value.CreationCutoff!=MmCollectionPolicy.CreationCutoff)
   throw new InvalidOperationException("MM_VM_OBSERVATION_SCOPE_REJECTED");
  var now=DateTimeOffset.UtcNow;
  return Task.FromResult(new MmRemoteReadback(value,"VM_SNAPSHOT_NOT_JOURNAL",now,value.IsFresh(now)));
 }
}
