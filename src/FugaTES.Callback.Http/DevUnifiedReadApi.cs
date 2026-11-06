using FugaTES.Aplicacao.Configuracao;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FugaTES.Persistencia.Consultas.UnifiedDev;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;

namespace FugaTES.Callback.Http;

/// <summary>
/// Os ingressos aprovados da API privada de leitura: ambiente, frase de aprovacao e porta.
///
/// OS TRES VALEM JUNTOS. A porta e a identidade do ingresso - e por ela que o tunel do desktop
/// chega - e a frase de aprovacao e o que o operador declara. Aceitar a frase do DEV numa porta
/// de QAS, ou o contrario, seria servir dados de um ambiente por um canal autorizado para outro.
///
/// Ate 06/11/2026 havia um so: a frase estava comparada com um literal e a porta 18762 escrita
/// dentro do CreateReadOnlyHostBuilder. O banco, o diretorio de MM e a origem SAP ja eram
/// derivados do ambiente - so o canal nao era.
///
/// A porta do QAS e 18763, vizinha da do DEV e fora das faixas ja usadas (18080, 18086, 18087).
/// Quem a reconhece do outro lado e o LocalReadTunnel, no desktop: as duas listas sao a mesma
/// decisao escrita em dois lugares, e mudar uma sem a outra deixa a tela sem conectar.
/// </summary>
internal sealed record LeituraPrivadaIngresso(string Ambiente, string Aprovacao, int Porta)
{
    private static readonly LeituraPrivadaIngresso[] Aprovados =
    [
        new("DEV110", "FUGATES-UNIFIED-POSTGRESQL-DEV",    18762),
        new("QAS120", "FUGATES-UNIFIED-POSTGRESQL-QAS120", 18763),
    ];

    /// <summary>O ingresso deste par, ou nulo quando a frase nao e a do ambiente.</summary>
    internal static LeituraPrivadaIngresso? Por(string? ambiente, string? aprovacao)
        => Array.Find(Aprovados, i => i.Ambiente == ambiente && i.Aprovacao == aprovacao);

    /// <summary>As portas servidas, para quem precisa reconhece-las sem saber o ambiente.</summary>
    internal static int[] Portas => Array.ConvertAll(Aprovados, i => i.Porta);
}

// Explicit DEV-only host mode. It does not configure inbound, callbacks, workers, Guardian or SAP.
public static class DevUnifiedReadApi
{
    public static bool Selected(string[] args)
    {
        if(!args.Any(a=>a.StartsWith("--dev-unified",StringComparison.Ordinal)))return false;
        if(args.Length!=1||args[0]!="--dev-unified-read-only")throw new InvalidOperationException("UNIFIED_DEV_READ_MODE_ARGUMENTS_REJECTED");
        return true;
    }
    public static async Task RunAsync()
    {
        if(!OperatingSystem.IsLinux())throw new InvalidOperationException("UNIFIED_DEV_READ_MODE_NOT_AUTHORIZED");
        // O AMBIENTE PRIMEIRO, porque e ele que diz qual frase e qual porta valem aqui. A
        // configuracao era lida mais abaixo; subiu porque a autorizacao depende dela.
        var configuracao=new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var ambienteDoProcesso=FugaTES.HostIntegracao.AcmEnvironmentBinding.Banco(configuracao);
        var ingresso=LeituraPrivadaIngresso.Por(ambienteDoProcesso.Nome,
            Environment.GetEnvironmentVariable("FUGATES_DEV_READ_APPROVAL"))
            ?? throw new InvalidOperationException("UNIFIED_DEV_READ_MODE_NOT_AUTHORIZED");
        var keyFile=Reference("FUGATES_DEV_READ_KEY_FILE");
        if(new FileInfo(keyFile).Length!=64 || (File.GetAttributes(keyFile)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0 ||
            (File.GetUnixFileMode(keyFile)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)
            throw new InvalidOperationException("UNIFIED_DEV_PRIVATE_KEY_REFERENCE_REJECTED");
        var key=File.ReadAllText(keyFile).Trim();
        var config=configuracao;
        var ambiente=ambienteDoProcesso;
        var connection=BuildConnection(config,Reference("R165AL_APPROVED_PASSFILE"),RoleDeLeitura(ambiente));
        await using var source=NpgsqlDataSourceFactory.CriarAcm(connection.ConnectionString,ambiente);
        // A role configurada atravessa o store: sem isto o guard de identidade compara current_user
        // com a role ACM compartilhada e recusa a identidade GET-only na partida.
        var store=new DevUnifiedReadStore(source,RoleDeLeitura(ambiente),ambiente);await store.IdentityAsync(CancellationToken.None);
        var builder=CreateReadOnlyHostBuilder(ingresso.Porta);
        await using var app=builder.Build();
        var mmDirectory=Environment.GetEnvironmentVariable("FUGATES_DEV_MM_SNAPSHOT_DIRECTORY");
        if(mmDirectory is not null&&mmDirectory!=(ambiente==AmbientesBanco.Dev110?"/var/lib/fugates-mm":"/var/lib/fugates-mm-qas"))throw new InvalidOperationException("MM_VM_OBSERVATION_DIRECTORY_REJECTED");
        Map(app,store,key,mmDirectory is null?null:new MmSnapshotReadSource(mmDirectory,AmbientesSap.Aprovados.Single(a=>a.Nome==ambiente.Nome)),ambiente);await app.RunAsync();
    }
    public static NpgsqlConnectionStringBuilder BuildConnection(IConfiguration config,string passfile,string? role=null)
    {
        var a=FugaTES.HostIntegracao.AcmEnvironmentBinding.Banco(config);role??=a.PapelAcm;
        if(AmbientesBanco.Por(a.Banco,role,a)!=a||role!=a.PapelAcm&&role!=a.PapelLeitura)throw new InvalidOperationException("UNIFIED_DEV_READ_ROLE_NOT_AUTHORIZED");
        return new(){Host="127.0.0.1",Port=5433,Database=a.Banco,SearchPath="fugates",Username=role,Passfile=passfile,IncludeErrorDetail=false,ApplicationName="FugaTES.UnifiedDev.ReadOnly",Options="-c default_transaction_read_only=on"};
    }
    /// <summary>
    /// A porta e parametro, com o DEV110 como padrao: quem ja chamava isto - testes e qualquer
    /// outro uso - continua recebendo exatamente o que recebia.
    /// </summary>
    public static WebApplicationBuilder CreateReadOnlyHostBuilder(int porta = 18762)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=[]});
        builder.Configuration.Sources.Clear();builder.Configuration.AddInMemoryCollection();builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:"+porta.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return builder;
    }
    // R164: identidade de banco desta API. O padrao continua sendo a role operacional
    // compartilhada, para que nada mude sem acao explicita do operador. A unica alternativa
    // aceita e a identidade GET-only dedicada; qualquer outro valor e recusado, de modo que
    // esta variavel nunca possa apontar a API de leitura para uma identidade com escrita.
    public const string RoleEnvironmentVariable = "FUGATES_DEV_READ_ROLE";
    public static string RoleDeLeitura(AmbienteBanco? ambiente=null)
    {
        ambiente??=AmbientesBanco.Dev110;
        var escolhida = Environment.GetEnvironmentVariable(RoleEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(escolhida)) return ambiente.PapelAcm;
        if (escolhida != ambiente.PapelLeitura)
            throw new InvalidOperationException("UNIFIED_DEV_READ_ROLE_NOT_AUTHORIZED");
        return escolhida;
    }

    static string Reference(string name)
    {
        var value=Environment.GetEnvironmentVariable(name);
        if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value)||value.Any(char.IsControl)||!File.Exists(value))
            throw new InvalidOperationException("UNIFIED_DEV_EXISTING_PROVIDER_REFERENCE_REQUIRED");
        return value;
    }
    public static void Map(WebApplication app,IDevUnifiedReadStore store,string key,IMmSnapshotReadSource? mm=null,AmbienteBanco? ambiente=null)
    {
        if(key.Length!=64||!key.All(Uri.IsHexDigit))throw new InvalidOperationException("UNIFIED_DEV_READ_KEY_INVALID");
        var expected=Encoding.UTF8.GetBytes(key);
        app.Use(async(context,next)=>
        {
            var peer=context.Connection.RemoteIpAddress;
            if(peer is null||!IPAddress.IsLoopback(peer)){context.Response.StatusCode=403;return;}
            var values=context.Request.Headers["X-R165AL-Read-Key"];
            if(values.Count!=1||values[0]?.Length!=64||!CryptographicOperations.FixedTimeEquals(expected,Encoding.UTF8.GetBytes(values[0]!)))
            {context.Response.StatusCode=401;return;}
            context.Response.Headers.CacheControl="no-store";
            try{await next(context);}
            catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested){}
            catch(PostgresException error) when(error.SqlState=="42501")
            {if(!context.Response.HasStarted){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{code="UNIFIED_DEV_READ_PRIVILEGE_REQUIRED"});}}
            catch{if(!context.Response.HasStarted){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{code="UNIFIED_DEV_READ_UNAVAILABLE"});}}
        });
        app.MapGet("/dev/read/mm",async(CancellationToken ct)=>
        {
            var identity=await store.IdentityAsync(ct);identity.Validate(ambiente??AmbientesBanco.Dev110);
            if(mm is null)return Results.Json(new{scope="mm",code="MM_VM_OBSERVATION_NOT_CONFIGURED",identity},statusCode:503);
            try
            {
                var value=await mm.ReadAsync(ct);
                return Results.Json(new DevReadEnvelope<FugaTES.Aplicacao.Modelos.Mm.MmRemoteReadback>(identity,value));
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception error)
            {
                // No exception text, document, path or credential crosses the diagnostic boundary.
                var code=error is System.Text.Json.JsonException?"MM_VM_OBSERVATION_DOCUMENT_REJECTED":
                    error is InvalidOperationException {Message:"MM_VM_OBSERVATION_SCOPE_REJECTED"}?"MM_VM_OBSERVATION_SCOPE_REJECTED":
                    error is InvalidOperationException {Message:"MM_VM_OBSERVATION_DOCUMENT_REJECTED" or "MM_CACHE_POLICY_INVALID" or "MM_CACHE_IDENTITY_INVALID" or "MM_CACHE_SCOPE_INVALID" or "MM_GUARDIAN_CACHE_INVALID" or "MM_CACHE_INVALID" or "MM_CACHE_SIZE_LIMIT"}?"MM_VM_OBSERVATION_DOCUMENT_REJECTED":
                    "MM_VM_OBSERVATION_UNAVAILABLE";
                return Results.Json(new{scope="mm",code,identity},statusCode:503);
            }
        });
        app.MapGet("/dev/read/identity",async(CancellationToken ct)=>await store.IdentityAsync(ct));
        app.MapGet("/dev/read/{resource}",async(string resource,string? selector,CancellationToken ct)=>
        {
            if(resource is not("contracts" or "monitor" or "detail" or "protocol" or "masterdata" or "toledo" or "toledo-json" or "toledo-sap-json"))return Results.NotFound();
            if(selector?.Length>200||selector?.Any(char.IsControl)==true)return Results.BadRequest(new{code="UNIFIED_DEV_FILTER_INVALID"});
            try{return Results.Json(await store.ReadAsync(resource,selector,ct));}
            catch(KeyNotFoundException){return Results.NotFound(new{code="UNIFIED_DEV_RECORD_NOT_FOUND"});}
            catch(ArgumentException){return Results.BadRequest(new{code="UNIFIED_DEV_FILTER_INVALID"});}
        });
    }
}
