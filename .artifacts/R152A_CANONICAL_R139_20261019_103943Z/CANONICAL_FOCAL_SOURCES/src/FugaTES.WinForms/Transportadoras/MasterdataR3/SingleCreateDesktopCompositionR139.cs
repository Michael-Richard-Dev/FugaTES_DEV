using System.Globalization;
using System.Security.Principal;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.WinForms.Runtime;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

public static class SingleCreateDesktopCompositionR139
{
    public const string BusinessPartner="20000014";
    static IConfiguration Load()=>CarrierDesktopProfile.Load(Path.Combine(AppContext.BaseDirectory,"FugaTES.carrier.json"));
    public static bool UnitaryMode(IConfiguration config)=>CarrierDesktopProfile.True(config,"Carrier:SingleCreate:Enabled");
    static string Sid()=>WindowsIdentity.GetCurrent().User?.Value??"";

    // No I/O. Removing the journal blocker here permits only an explicit read-only preflight,
    // never CREATE. The physical preflight supplies attestation only after real successful checks.
    public static ProntidaoSingleCreateR122 Prerequisites(IConfiguration config,string bp,string sid,DateTimeOffset now)
    {
        var blocks=SingleCreateDesktopGateR122.Avaliar(bp,now,sid,config).Bloqueios
            .Where(b=>b!="SINGLE_CREATE_JOURNAL_RUNTIME_NOT_ATTESTED").ToList();
        if(bp!=BusinessPartner||CarrierManualReadOnlyProfile.Identity(config,bp)?.Codigo!=BusinessPartner)blocks.Add("R41_FUNCTIONAL_SCOPE_INVALID");
        if(!CarrierDesktopProfile.True(config,"Carrier:SingleCreate:SpecificCreateAuthorized")||
            string.IsNullOrWhiteSpace(config["Carrier:SingleCreate:ExecutionApprovalReference"]))
            blocks.Add("SINGLE_CREATE_EXECUTION_NOT_AUTHORIZED");
        try{CarrierManualReadOnlyProfile.Validate(config,bp,sid,now);}catch{blocks.Add("SINGLE_CREATE_MANUAL_PROFILE_NOT_PROVEN");}
        foreach(var key in new[]{"FUGATES_CARRIER_SYNC_ENABLED","FUGATES_CARRIER_WORKER_ENABLED"})
            if(!bool.TryParse(config[key],out var enabled)||enabled||bool.TryParse(Environment.GetEnvironmentVariable(key),out var env)&&env)
                blocks.Add("SINGLE_CREATE_AUTOMATIC_ROUTE_NOT_DISABLED");
        return new(blocks.Count==0,blocks.Distinct().ToArray());
    }

    public static SingleCreateDesktopControllerR139 Create(OrquestradorCompartilhadoMasterdataR3 shared)
        =>new(shared,()=>UnitaryMode(Load()),bp=>Prerequisites(Load(),bp,Sid(),DateTimeOffset.UtcNow),PrepareAsync);

    static async Task<ISingleCreateDesktopSessionR139> PrepareAsync(string bp,CancellationToken ct)
    {
        var config=Load();var sid=Sid();
        if(!Prerequisites(config,bp,sid,DateTimeOffset.UtcNow).Pronto)
            throw new DesktopStartupException("SINGLE_CREATE_PREREQUISITES_BLOCKED");
        var profile=SingleCreateDatabaseProfileR139.Load(config);
        var runtime=DesktopRuntime.Current??throw new DesktopStartupException("SINGLE_CREATE_DESKTOP_DATABASE_NOT_READY");
        var source=runtime.CreateSingleCreateSource(profile);
        try
        {
            await VerifyPhysicalIdentityAsync(source,profile,ct).ConfigureAwait(false);
            if(!await SingleCreateDesktopBindingR41.JournalReadyAsync(source,ct).ConfigureAwait(false))
                throw new DesktopStartupException("SINGLE_CREATE_JOURNAL_NOT_READY");
            return new Session(source,config,sid);
        }
        catch{await source.DisposeAsync().ConfigureAwait(false);throw;}
    }

    // SELECT-only preflight; no carrier_sync_* dependency. Actual execution is not part of R139 QA.
    public const string PhysicalIdentitySql="""
        select current_database()=@database and current_user=@role and current_schema()='fugates'
          and not has_schema_privilege(current_user,'fugates','CREATE')
          and not (select rolsuper or rolcreatedb or rolcreaterole from pg_roles where rolname=current_user)
          and to_regclass('fugates.carrier_single_create_journal') is not null
          and not exists (
            select 1 from unnest(array['attempt_id','bp','guardian_code']) required(name)
            where not exists (
              select 1 from pg_index i join pg_attribute a on a.attrelid=i.indrelid and a.attnum=i.indkey[0]
              where i.indrelid=to_regclass('fugates.carrier_single_create_journal')
                and i.indisunique and i.indisvalid and i.indisready and i.indnkeyatts=1
                and i.indpred is null and i.indexprs is null and a.attname=required.name))
          and (select count(*) from pg_constraint where conrelid=to_regclass('fugates.carrier_single_create_journal')
               and contype='c' and convalidated)>=4
        """;
    static async Task VerifyPhysicalIdentityAsync(NpgsqlDataSource source,SingleCreateDatabaseProfileR139 profile,CancellationToken ct)
    {
        await using var connection=await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx=await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using(var ro=new NpgsqlCommand("set transaction read only",connection,tx))await ro.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await using var cmd=new NpgsqlCommand(PhysicalIdentitySql,connection,tx);
        cmd.Parameters.AddWithValue("database",profile.Database);cmd.Parameters.AddWithValue("role",profile.Role);
        if(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
            throw new DesktopStartupException("SINGLE_CREATE_PHYSICAL_IDENTITY_OR_JOURNAL_INVALID");
        await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
    }

    sealed class Session(NpgsqlDataSource source,IConfiguration config,string sid):ISingleCreateDesktopSessionR139
    {
        int used;bool disposed;
        readonly SingleCreateProfileLeaseR139 profileLease=new(config,Load,
            current=>Prerequisites(current,BusinessPartner,sid,DateTimeOffset.UtcNow).Pronto);
        bool Gate()=>!disposed&&profileLease.IsValid;
        public ProntidaoSingleCreateR122 Readiness(string bp)
        {
            if(bp!=BusinessPartner||disposed||Volatile.Read(ref used)!=0)return new(false,["SINGLE_CREATE_PREFLIGHT_REQUIRED"]);
            if(!Gate())return new(false,["SINGLE_CREATE_PROFILE_CHANGED_OR_REVOKED"]);
            return SingleCreateDesktopGateR122.Avaliar(bp,DateTimeOffset.UtcNow,sid,config,journalFisicoComprovado:true);
        }
        public async Task<SingleCreateResultR31> ExecuteAsync(Guid attemptId,CancellationToken ct)
        {
            SingleCreateResultR31 Block(string code)=>new(attemptId,SingleCreateStateR31.BLOCKED,code);
            if(!Gate()||Interlocked.CompareExchange(ref used,1,0)!=0)return Block("SINGLE_CREATE_NOT_AUTHORIZED_OR_ALREADY_ATTEMPTED");
            var identity=CarrierManualReadOnlyProfile.Identity(config,BusinessPartner);
            var manualContract=CarrierProtocolBinding.BuildGuardianManualContract(config);
            if(identity is null||manualContract is null)return Block("SINGLE_CREATE_IDENTITY_NOT_PROVEN");
            var sapUri=new Uri(config["Sap:BaseUrl"]!);var guardianUri=new Uri(config["Guardian:BaseUrl"]!);
            using var http=new HttpClient(new SingleCreateHttpGuardR139(sapUri,guardianUri,Gate,new HttpClientHandler{AllowAutoRedirect=false}));
            var timeout=TimeSpan.FromSeconds(int.Parse(config["Carrier:ManualReadOnly:TimeoutSeconds"]!,CultureInfo.InvariantCulture));
            var client=new ClienteHttpParceiroNegocioSap(http,new OpcoesSap{BaseUrl=sapUri,SapClient="110",Timeout=timeout},new CarrierServiceSapAuthentication(config));
            var sap=new SapBpReadAdapterR20B(client);
            using var prepared=await new GuardianSingleCreatePreparationR41(sap).PrepareAsync(
                SingleCarrierDecisionR41.Approved20000014(config[SingleCreateDesktopGateR122.ChaveCallback]!,
                    CarrierDesktopProfile.True(config,SingleCreateDesktopGateR122.ChaveCallbackAprovado)),ct).ConfigureAwait(false);
            if(!prepared.Ready)return Block(prepared.Code);
            var options=new GuardianHttpOptions{BaseUrl=guardianUri,Timeout=timeout};
            var tokens=new GuardianTokenClient(http,options,new CarrierServiceGuardianCredentials(config));
            var reader=new GuardianCarrierManualReadOnlyAdapter(http,options,tokens,manualContract);
            // Actual call missing in R135B: same dedicated source, no fallback to general Carrier pool.
            var binding=await SingleCreateDesktopBindingR41.BindAsync(config,prepared,reader,
                SingleCreateGatewayCompositionR42.Criar(http,options,tokens),source,Gate,ct:ct).ConfigureAwait(false);
            if(!binding.Ready)return Block(binding.Code);
            var until=DateTimeOffset.Parse(config["Carrier:Authorization:UntilUtc"]!,CultureInfo.InvariantCulture);
            var approval=new SingleCreateApprovalR31(BusinessPartner,identity.Codigo,identity.IdAssinatura,
                config["Carrier:SingleCreate:ExecutionApprovalReference"]!,until,
                BpConfirmed:true,CodeApproved:true,HmlSignatureBound:manualContract.SignatureVerified,
                PayloadContractComplete:prepared.Ready,ScopedAbsenceApproved:manualContract.Scoped404Proven,
                PostgreSqlBootstrapAttested:true,SpecificCreateAuthorized:Gate());
            return await binding.UseCase!.ExecuteAsync(prepared.CreateRequest(attemptId,identity,approval),ct).ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync(){disposed=true;await source.DisposeAsync().ConfigureAwait(false);}
    }
}

// An attestation belongs to one exact profile snapshot. Reload before every gate and HTTP send.
// File removal, parse failure, identity change or revocation invalidates it; no silent rebind.
public sealed class SingleCreateProfileLeaseR139
{
    readonly KeyValuePair<string,string?>[] approved;
    readonly Func<IConfiguration> reload;
    readonly Func<IConfiguration,bool> authorize;
    static KeyValuePair<string,string?>[] Values(IConfiguration c)=>c.AsEnumerable().OrderBy(p=>p.Key,StringComparer.Ordinal).ToArray();
    public SingleCreateProfileLeaseR139(IConfiguration approvedProfile,Func<IConfiguration> reload,Func<IConfiguration,bool> authorize)
    {approved=Values(approvedProfile);this.reload=reload;this.authorize=authorize;}
    public bool IsValid
    {
        get{try{var current=reload();return approved.SequenceEqual(Values(current))&&authorize(current);}catch{return false;}}
    }
}

// Exact unitary scope, session-owned client. No PUT, no unscoped SAP/Guardian query.
public sealed class SingleCreateHttpGuardR139(Uri sap,Uri guardian,Func<bool> writeGate,HttpMessageHandler inner):DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var u=request.RequestUri??throw new CarrierGatewayException("SINGLE_CREATE_HTTP_URI_INVALID");
        bool safe=u.Scheme=="https"&&u.UserInfo.Length==0&&u.Fragment.Length==0;
        bool sapRead=u.GetLeftPart(UriPartial.Authority)==sap.GetLeftPart(UriPartial.Authority)&&request.Method==HttpMethod.Get&&
            u.AbsolutePath=="/sap/opu/odata/sap/API_BUSINESS_PARTNER/A_BusinessPartner('20000014')";
        bool guardianOrigin=u.GetLeftPart(UriPartial.Authority)==guardian.GetLeftPart(UriPartial.Authority);
        bool read=guardianOrigin&&request.Method==HttpMethod.Get&&u.AbsolutePath=="/api/v0/Guardian/Transportadora/20000014";
        bool auth=guardianOrigin&&request.Method==HttpMethod.Post&&u.AbsolutePath=="/api/v2/CreateToken";
        bool write=guardianOrigin&&request.Method==HttpMethod.Post&&u.AbsolutePath=="/api/v0/Guardian/Transportadora"&&writeGate();
        if(!safe||!sapRead&&!read&&!auth&&!write)throw new CarrierGatewayException("SINGLE_CREATE_HTTP_ROUTE_OR_WRITE_FORBIDDEN");
        return base.SendAsync(request,ct);
    }
}
