using FugaTES.Aplicacao.Configuracao;
using Npgsql;
namespace FugaTES.Persistencia.Mm;
/// <summary>Dedicated non-pooled physical session; a lost lock is never reacquired in-place.</summary>
public sealed class MmWorkerLease:IAsyncDisposable
{
 private readonly NpgsqlConnection connection;
 private readonly SemaphoreSlim gate=new(1,1);
 private int held;
 private MmWorkerLease(NpgsqlConnection connection){this.connection=connection;held=1;}
 public bool IsHeld=>Volatile.Read(ref held)==1&&connection.State==System.Data.ConnectionState.Open;
 public static async Task<MmWorkerLease> AcquireAsync(string connectionString,CancellationToken ct,AmbienteBanco? ambiente=null)
 {
  var options=new NpgsqlConnectionStringBuilder(connectionString){Pooling=false,Multiplexing=false,CommandTimeout=5};
  var c=new NpgsqlConnection(options.ConnectionString);
  try
  {
   await c.OpenAsync(ct);await RegistroProtocolosMmPostgreSql.ConferirAsync(c,null,ct,ambiente);
   await using var cmd=new NpgsqlCommand("select pg_try_advisory_lock(3000,5433001)",c);
   if(await cmd.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("MM_WORKER_ALREADY_RUNNING");
   return new(c);
  }
  catch{await c.DisposeAsync();throw;}
 }
 public async Task<bool> CheckAsync(CancellationToken ct)
 {
  if(!IsHeld)return false;
  await gate.WaitAsync(ct);
  try
  {
   if(!IsHeld)return false;
   await using var cmd=new NpgsqlCommand("select exists(select 1 from pg_locks where pid=pg_backend_pid() and locktype='advisory' and classid=3000 and objid=5433001 and objsubid=2 and granted)",connection){CommandTimeout=5};
   if(await cmd.ExecuteScalarAsync(ct) is true)return true;
  }
  catch(Exception) when(!ct.IsCancellationRequested){}
  finally{gate.Release();}
  Interlocked.Exchange(ref held,0);return false;
 }
 public async ValueTask DisposeAsync()
 {
  Interlocked.Exchange(ref held,0);await gate.WaitAsync();
  try{await connection.DisposeAsync();}finally{gate.Release();gate.Dispose();}
 }
}
