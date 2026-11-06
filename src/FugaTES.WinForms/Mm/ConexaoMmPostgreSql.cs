using System.Net;
using System.Net.Sockets;
using FugaTES.Persistencia.Mm;
using FugaTES.WinForms.Runtime;
using Npgsql;
namespace FugaTES.WinForms.Mm;
/// <summary>Binding MM independente, com o mecanismo de túnel/credencial já usado pelo SD.</summary>
public sealed class ConexaoMmPostgreSql:IDisposable
{
    public NpgsqlDataSource Fonte {get;}
    private readonly DesktopTunnel tunnel;
    private ConexaoMmPostgreSql(NpgsqlDataSource source,DesktopTunnel tunnel){Fonte=source;this.tunnel=tunnel;}
    public static void ValidarPerfil(DesktopConfiguration profile)
    {
        profile.Validar();
        if(profile.Database.Database!="fugates_dev"||profile.Database.Username!=RegistroProtocolosMmPostgreSql.Role||profile.Database.Schema!="fugates"
           ||!profile.Ssh.Enabled||profile.Ssh.Host!="192.168.17.40"||profile.Ssh.RemoteDatabaseHost!="127.0.0.1"||profile.Ssh.RemoteDatabasePort!=5433)
            throw new InvalidOperationException("MM_DATABASE_PROFILE_REJECTED");
    }
    public static async Task<ConexaoMmPostgreSql> AbrirAsync(string directory,CancellationToken ct)
    {
        var profile=DesktopConfiguration.Load(Path.Combine(directory,"FugaTES.mm.json"));ValidarPerfil(profile);
        var password=WindowsCredentialProvider.ReadPassword(profile.Database.CredentialTarget,profile.Database.Username);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port;
        try{port=((IPEndPoint)listener.LocalEndpoint).Port;}finally{listener.Stop();}
        profile=profile with{Database=profile.Database with{Port=port}};
        var tunnel=DesktopTunnel.Start(profile);NpgsqlDataSource? source=null;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(profile.ReadinessSeconds));
            while(true)
            {
                if(tunnel.HasExited)throw new InvalidOperationException("MM_TUNNEL_UNAVAILABLE");
                using var socket=new TcpClient();
                try{await socket.ConnectAsync(IPAddress.Loopback,port,timeout.Token);break;}
                catch(SocketException){await Task.Delay(200,timeout.Token);}
            }
            source=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder{Host="127.0.0.1",Port=port,Database="fugates_dev",Username=RegistroProtocolosMmPostgreSql.Role,
                Password=password,Timeout=5,CommandTimeout=15,IncludeErrorDetail=false,ApplicationName="FugaTES.MM.Protocolo",MaxPoolSize=2}.ConnectionString);
            // Registry checks the physical identity again on the SAME connection used for every write.
            await new RegistroProtocolosMmPostgreSql(source).LerAsync(timeout.Token);
            return new(source,tunnel);
        }
        catch{source?.Dispose();tunnel.Dispose();throw;}
    }
    public void Dispose(){Fonte.Dispose();tunnel.Dispose();}
}
