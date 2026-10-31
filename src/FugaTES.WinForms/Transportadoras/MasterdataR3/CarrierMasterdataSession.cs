using System.Globalization;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.Aplicacao.Transportadoras.SingleCreateR31;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;
using FugaTES.Integracao.Guardian.Transportadoras;
using FugaTES.Integracao.SAP.BusinessPartner;
using FugaTES.Integracao.SAP.Configuracao;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Persistencia.Transportadoras;
using FugaTES.WinForms.Runtime;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FugaTES.SingleCreate.R41.Tests")]

namespace FugaTES.WinForms.Transportadoras.MasterdataR3;

public sealed class CarrierMasterdataSession : IAsyncDisposable
{
    readonly NpgsqlDataSource source;
    readonly DesktopTunnel? tunnel;
    readonly HttpClient http;
    readonly CarrierMasterdataHttpGuard transport;
    readonly IConfiguration config;
    readonly KeyValuePair<string,string?>[] profileSnapshot;
    readonly string profilePath,sid,bp;
    readonly CarrierManualIdentity identity;
    readonly ISapBpMasterReaderR20B sap;
    readonly IGuardianCarrierManualReadOnly guardian;
    readonly GuardianHttpOptions options;
    readonly GuardianTokenClient tokens;
    readonly Func<CancellationToken,Task<IReadOnlyList<CarrierCreateHistoryEntry>>> readHistory;
    readonly Func<PreparedSingleCreateR41,SingleCreateRequestR31,GuardianCarrierHmlContract,CancellationToken,Task<SingleCreateResultR31>> executePrepared;
    readonly Func<bool>? leaseOverride;
    PreparedSingleCreateR41? prepared;
    bool unblocked,disposed,selectionValid=true;
    bool historyVerified;
    string? observedCode;
    long? observedSequence;
    int dispatched;
    public string BusinessPartner=>bp;
    public bool SelectionValid=>selectionValid;
    public int? CreateHttpStatus=>transport?.CreateHttpStatus;
    public string? ReturnedCode=>transport?.ReturnedCode??observedCode??History.FirstOrDefault(x=>x.Status=="CONFIRMED")?.GuardianCode;
    public long? ReturnedSequence=>transport?.ReturnedSequence??observedSequence;
    public SingleCreateResultR31? LastResult {get;private set;}
    public string HistoryReadStatus {get;private set;}="NOT_QUERIED";
    public string Name {get;private set;}="";
    public string LegalName {get;private set;}="";
    public string Cnpj {get;private set;}="";
    public string SapStatus {get;private set;}="Não consultado";
    public string GuardianStatus {get;private set;}="Não consultado";
    public string JsonPreview=>prepared?.Ready==true?Encoding.UTF8.GetString(prepared.CopyBody()):"Payload não preparado.";
    public IReadOnlyList<CarrierCreateHistoryEntry> History {get;private set;}=[];
    public string CallbackStatus=>"Não observado neste módulo; confirmação síncrona e callback são estados separados.";
    public bool CanCreate=>!disposed&&selectionValid&&historyVerified&&observedCode is null&&Volatile.Read(ref dispatched)==0&&unblocked&&prepared?.Ready==true&&
        GuardianStatus=="ABSENT_CONFIRMED"&&CarrierTargetPolicy.HistoryAllowsCreate(History)&&LeaseValid();
    public void InvalidateSelection()=>selectionValid=false;
    public string CreateReadiness=>History.Any(x=>x.Status=="CONFIRMED")||LastResult?.State==SingleCreateStateR31.CONFIRMED?"EXISTING_CONFIRMED — não reenviar.":
        observedCode is not null?"EXISTING_OBSERVED — não reenviar; consulta divergente exige reconciliação.":
        LastResult?.State==SingleCreateStateR31.INDETERMINATE?"INDETERMINATE — reconciliação necessária; não reenviar.":
        History.Count>0?"Tentativa existente: intervenção/reconciliação necessária; não reenviar.":
        CanCreate?"CREATE manual autorizado na janela vigente.":
        "CREATE desabilitado: requer autorização específica BP/código/SID/janela e callback autenticado.";

    CarrierMasterdataSession(NpgsqlDataSource source,DesktopTunnel? tunnel,IConfiguration config,string path,string bp,string sid)
    {
        this.source=source;this.tunnel=tunnel;this.config=config;profilePath=path;this.bp=bp;this.sid=sid;
        readHistory=ct=>new CarrierCreateHistory(source).ReadAsync(bp,CarrierTargetPolicy.ReadIdentity(config,bp).Codigo,ct);
        executePrepared=(body,request,proven,ct)=>body.CreateBoundUseCase(guardian!,http!,options!,tokens!,proven,new SingleCreateJournalPostgreSqlR31(source),LeaseValid).ExecuteAsync(request,ct);
        profileSnapshot=Values(config);
        identity=CarrierTargetPolicy.ReadIdentity(config,bp);
        var sapUri=new Uri(config["Sap:BaseUrl"]!);var guardianUri=new Uri(config["Guardian:BaseUrl"]!);
        var timeout=TimeSpan.FromSeconds(int.Parse(config["Carrier:ManualReadOnly:TimeoutSeconds"]!,CultureInfo.InvariantCulture));
        transport=new(sapUri,guardianUri,bp,identity.Codigo,identity.IdAssinatura,
            ()=>!disposed&&LeaseValid(),new HttpClientHandler{AllowAutoRedirect=false});
        http=new(transport){Timeout=timeout};
        sap=new SapBpReadAdapterR20B(new ClienteHttpParceiroNegocioSap(http,new OpcoesSap{BaseUrl=sapUri,SapClient="110",Timeout=timeout},new CarrierServiceSapAuthentication(config)));
        options=new(){BaseUrl=guardianUri,Timeout=timeout};tokens=new(http,options,new CarrierServiceGuardianCredentials(config));
        var manual=CarrierProtocolBinding.BuildGuardianManualContract(config)??throw new DesktopStartupException("MASTERDATA_READ_CONTRACT_NOT_PROVEN");
        guardian=new GuardianCarrierManualReadOnlyAdapter(http,options,tokens,manual with
        {LookupRoute="/api/v0/Guardian/Transportadora/{bp}?idAssinatura="+Uri.EscapeDataString(identity.IdAssinatura)});
    }
    internal CarrierMasterdataSession(IConfiguration config,string bp,ISapBpMasterReaderR20B sap,
        IGuardianCarrierManualReadOnly guardian,Func<CancellationToken,Task<IReadOnlyList<CarrierCreateHistoryEntry>>> history,
        Func<PreparedSingleCreateR41,SingleCreateRequestR31,GuardianCarrierHmlContract,CancellationToken,Task<SingleCreateResultR31>> execute,Func<bool> lease)
    {
        this.config=config;this.bp=bp;this.sap=sap;this.guardian=guardian;readHistory=history;executePrepared=execute;leaseOverride=lease;
        identity=CarrierTargetPolicy.ReadIdentity(config,bp);sid="OFFLINE";profilePath="";profileSnapshot=Values(config);
        source=null!;http=null!;transport=null!;options=null!;tokens=null!;
    }
    static KeyValuePair<string,string?>[] Values(IConfiguration c)=>c.AsEnumerable().OrderBy(x=>x.Key,StringComparer.Ordinal).ToArray();
    bool LeaseValid()
    {
        if(leaseOverride is not null)return leaseOverride();
        try
        {
            var current=CarrierDesktopProfile.Load(profilePath);
            return profileSnapshot.SequenceEqual(Values(current))&&CarrierTargetPolicy.CanCreate(current,bp,identity.Codigo,
                WindowsIdentity.GetCurrent().User?.Value??"",DateTimeOffset.UtcNow);
        }
        catch{return false;}
    }
    public static async Task<CarrierMasterdataSession> OpenAsync(string bp,CancellationToken ct)
    {
        var path=Path.Combine(AppContext.BaseDirectory,"FugaTES.masterdata.json");
        var c=CarrierDesktopProfile.Load(path);var sid=WindowsIdentity.GetCurrent().User?.Value??"";
        CarrierTargetPolicy.ValidateRead(c,bp,sid,DateTimeOffset.UtcNow);
        var db=DesktopConfiguration.Load(Path.Combine(AppContext.BaseDirectory,"FugaTES.masterdata.runtime.json"));db.Validar();
        var d=db.Database;
        var physical=new SingleCreateDatabaseProfileR139(db.Environment,true,"R163A_DEDICATED_MASTERDATA",d.Host,d.Port,d.Database,d.Username,d.Schema);
        if(MasterdataHmlPhysicalGuardR153A.ValidarPerfil(physical) is {} invalid)throw new DesktopStartupException(invalid);
        if(db.Ssh.Enabled&&(db.Ssh.RemoteDatabaseHost!="127.0.0.1"||db.Ssh.RemoteDatabasePort!=5434))
            throw new DesktopStartupException("MASTERDATA_TUNNEL_TARGET_INVALID");
        DesktopTunnel? tunnel=null;NpgsqlDataSource? source=null;
        try
        {
            static async Task<bool> Listening(CancellationToken token)
            {try{using var tcp=new TcpClient();await tcp.ConnectAsync("127.0.0.1",15434,token);return true;}catch(OperationCanceledException){throw;}catch{return false;}}
            if(!await Listening(ct))
            {
                tunnel=DesktopTunnel.Start(db);
                var until=DateTimeOffset.UtcNow.AddSeconds(db.ReadinessSeconds);
                while(!await Listening(ct))
                {if(tunnel.HasExited||DateTimeOffset.UtcNow>=until)throw new DesktopStartupException("MASTERDATA_TUNNEL_NOT_READY");await Task.Delay(150,ct);}
            }
            var password=WindowsCredentialProvider.ReadPassword(d.CredentialTarget,d.Username);
            var connection=physical.Options(db.ConnectionString(password)).ConnectionString;
            source=MasterdataHmlDataSourceR147B.Criar(connection);
            await MasterdataHmlPhysicalGuardR153A.VerifyAsync(source,physical,ct);
            await VerifyJournalAsync(source,ct);
            return new(source,tunnel,c,path,bp,sid);
        }
        catch{if(source is not null)await source.DisposeAsync();tunnel?.Dispose();throw;}
    }
    public const string JournalAttestationSql="""
        select current_schema()='fugates'
          and not has_schema_privilege(current_user,'fugates','CREATE')
          and not (select rolsuper or rolcreatedb or rolcreaterole from pg_roles where rolname=current_user)
          and case when to_regclass('fugates.carrier_single_create_journal') is null then false else
            has_table_privilege(current_user,'fugates.carrier_single_create_journal','SELECT')
            and has_table_privilege(current_user,'fugates.carrier_single_create_journal','INSERT')
            and has_table_privilege(current_user,'fugates.carrier_single_create_journal','UPDATE')
            and not has_table_privilege(current_user,'fugates.carrier_single_create_journal','DELETE,TRUNCATE') end
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
    static async Task VerifyJournalAsync(NpgsqlDataSource source,CancellationToken ct)
    {
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var tx=await connection.BeginTransactionAsync(ct);
        try{
            await using(var ro=new NpgsqlCommand("set transaction read only",connection,tx))await ro.ExecuteNonQueryAsync(ct);
            await using var command=new NpgsqlCommand(JournalAttestationSql,connection,tx);
            if(await command.ExecuteScalarAsync(ct) is not true)throw new DesktopStartupException("MASTERDATA_JOURNAL_NOT_ATTESTED");
        }finally{await tx.RollbackAsync(CancellationToken.None);}
    }
    async Task RefreshHistoryAsync(CancellationToken ct)
    {
        try
        {
            var rows=await readHistory(ct);
            // An incomplete observation cannot erase a known attempt or downgrade confirmation.
            History=rows.Concat(History).GroupBy(x=>x.AttemptId)
                .Select(g=>g.FirstOrDefault(x=>x.Status=="CONFIRMED")??g.First()).ToArray();
            historyVerified=true;HistoryReadStatus="PASS";
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){historyVerified=false;throw;}
        catch{historyVerified=false;HistoryReadStatus="MASTERDATA_HISTORY_READ_FAILED — histórico anterior preservado";}
    }
    public async Task ConsultAsync(CancellationToken ct)
    {
        unblocked=false;prepared?.Dispose();prepared=null;
        await RefreshHistoryAsync(ct);
        SapBpReadResultR20B? data=null;
        try
        {
        data=await sap.ReadAsync(bp,ct);
        if(!data.Complete||data.BusinessPartner!=bp)throw new DesktopStartupException("MASTERDATA_SAP_INCOMPLETE");
        var raw=data.RawSapData;
        if(GuardianSingleCreatePreparationR41.Text(raw,"BusinessPartner")!=bp||GuardianSingleCreatePreparationR41.DuplicatesOrContinuation(raw))throw new DesktopStartupException("MASTERDATA_SAP_IDENTITY_OR_CONTINUATION_INVALID");
        Name=GuardianSingleCreatePreparationR41.Text(raw,"BusinessPartnerName")??"";
        LegalName=GuardianSingleCreatePreparationR41.Text(raw,"BusinessPartnerFullName")??"";
        unblocked=CarrierTargetPolicy.SapUnblocked(raw);
        SapStatus=unblocked?"SAP consultado — não bloqueado":"SAP bloqueado ou situação não comprovada";
        try
        {
            var crm=raw.GetProperty("to_BusinessPartnerRole").GetProperty("results").EnumerateArray().Where(r=>GuardianSingleCreatePreparationR41.Text(r,"BusinessPartnerRole")=="CRM010").ToArray();
            var roleValid=crm.Length==1&&GuardianSingleCreatePreparationR41.Text(crm[0],"BusinessPartner")==bp&&
                new FugaTES.Aplicacao.Transportadoras.SnapshotsR24.Crm010BusinessDatePolicyR25T().Evaluate(
                    CarrierManualODataDateParser.Parse(GuardianSingleCreatePreparationR41.Text(crm[0],"ValidFrom")),CarrierManualODataDateParser.Parse(GuardianSingleCreatePreparationR41.Text(crm[0],"ValidTo")),DateTimeOffset.UtcNow).Status==FugaTES.Aplicacao.Transportadoras.SnapshotsR24.AvaliacaoCrm010R24.EVALUATED_VALID;
            var taxes=raw.GetProperty("to_BusinessPartnerTax").GetProperty("results").EnumerateArray().ToArray();
            var br1=taxes.Where(t=>GuardianSingleCreatePreparationR41.Text(t,"BPTaxType")=="BR1").ToArray();
            if(taxes.Any(t=>GuardianSingleCreatePreparationR41.Text(t,"BusinessPartner")!=bp)||br1.Length!=1)throw new InvalidOperationException();
            var shortTax=GuardianSingleCreatePreparationR41.Text(br1[0],"BPTaxNumber");var longTax=GuardianSingleCreatePreparationR41.Text(br1[0],"BPTaxLongNumber");
            var conflict=!string.IsNullOrWhiteSpace(shortTax)&&!string.IsNullOrWhiteSpace(longTax)&&shortTax!=longTax;
            Cnpj=string.IsNullOrWhiteSpace(longTax)?shortTax??"":longTax;
            var fiscalValid=!conflict&&GuardianSingleCreatePreparationR41.CnpjValid(Cnpj);
            SapStatus+=$" | CRM010: {(roleValid?"VALID":"UNVERIFIED/INVALID")} | BR1/CNPJ: {(fiscalValid?"VALIDATED":"UNVERIFIED/INVALID")}";
            unblocked=unblocked&&roleValid&&fiscalValid;
        }
        catch{Cnpj="";unblocked=false;SapStatus+=" | CRM010/BR1 incompleto ou não comprovado";}
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(DesktopStartupException e){unblocked=false;SapStatus=e.Code;}
        catch{unblocked=false;SapStatus="MASTERDATA_SAP_READ_FAILED";}
        try
        {
        var lookup=await guardian.ConsultarCodigoAsync(identity,ct);
        GuardianStatus=lookup.State==CarrierLookupState.Unique&&lookup.Codigo==identity.Codigo?"EXISTS":
            lookup.State==CarrierLookupState.Absent&&lookup.Ausencia404Comprovada?"ABSENT_CONFIRMED":"INCONCLUSIVE";
        if(GuardianStatus=="EXISTS")
        {
            observedCode=lookup.Codigo;
            if(lookup.Campos.TryGetValue("sequencial",out var value)&&long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var sequence)&&sequence>0)
                observedSequence=sequence;
        }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch{GuardianStatus="MASTERDATA_GUARDIAN_READ_FAILED — não significa ausência";}
        if(unblocked&&data is not null&&historyVerified&&observedCode is null&&CarrierTargetPolicy.HistoryAllowsCreate(History)&&GuardianStatus=="ABSENT_CONFIRMED")
        {
            var target="Masterdata:Create:Targets:"+bp+":";
            if(int.TryParse(config[target+"Estado"],out var estado))
                prepared=await new GuardianSingleCreatePreparationR41(new CachedSap(data)).PrepareAsync(new(bp,identity.Codigo,estado,
                    config["Masterdata:Create:CallbackUrl"]??"",CarrierDesktopProfile.True(config,target+"CodeApproved"),
                    CarrierDesktopProfile.True(config,target+"EstadoApproved"),CarrierDesktopProfile.True(config,"Masterdata:Create:CallbackApproved"),
                    CarrierDesktopProfile.True(config,target+"PayloadContractApproved"),MasterdataPrivateHttpBindingR159B.FromConfiguration(config)),ct);
        }
    }
    public async Task<SingleCreateResultR31> CreateAsync(CancellationToken ct)
    {
        var attempt=Guid.NewGuid();
        if(!CanCreate||Interlocked.CompareExchange(ref dispatched,1,0)!=0)
            return new(attempt,SingleCreateStateR31.BLOCKED,"MASTERDATA_CREATE_NOT_AUTHORIZED_OR_ALREADY_ATTEMPTED");
        await RefreshHistoryAsync(ct);
        if(!historyVerified)return new(attempt,SingleCreateStateR31.BLOCKED,"MASTERDATA_HISTORY_READ_FAILED");
        if(!CarrierTargetPolicy.HistoryAllowsCreate(History))return new(attempt,SingleCreateStateR31.BLOCKED,"PENDING_OR_REPLAY_RECONCILIATION_REQUIRED");
        var proven=CarrierProtocolBinding.BuildGuardianContract(config);
        if(proven is null)return new(attempt,SingleCreateStateR31.BLOCKED,"MASTERDATA_GUARDIAN_CONTRACT_UNPROVEN");
        var target="Masterdata:Create:Targets:"+bp+":";
        // Refresh fiscal data, eligibility and validity at the manual dispatch, not only at preview.
        var current=await sap.ReadAsync(bp,ct);
        if(!current.Complete||current.BusinessPartner!=bp||!CarrierTargetPolicy.SapUnblocked(current.RawSapData))
            return new(attempt,SingleCreateStateR31.BLOCKED,"MASTERDATA_SAP_REVALIDATION_FAILED");
        using var refreshed=await new GuardianSingleCreatePreparationR41(new CachedSap(current)).PrepareAsync(new(bp,identity.Codigo,
            int.Parse(config[target+"Estado"]!,CultureInfo.InvariantCulture),config["Masterdata:Create:CallbackUrl"]!,true,true,true,true,MasterdataPrivateHttpBindingR159B.FromConfiguration(config)),ct);
        if(!refreshed.Ready)return new(attempt,SingleCreateStateR31.BLOCKED,refreshed.Code);
        var preview=prepared!.CopyBody();var latest=refreshed.CopyBody();
        try
        {
            if(!preview.AsSpan().SequenceEqual(latest))
                return new(attempt,SingleCreateStateR31.BLOCKED,"MASTERDATA_PREVIEW_CHANGED_REVIEW_REQUIRED");
        }
        finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(preview);System.Security.Cryptography.CryptographicOperations.ZeroMemory(latest);}
        var approval=new SingleCreateApprovalR31(bp,identity.Codigo,identity.IdAssinatura,config[target+"ExecutionApprovalReference"]!,
            DateTimeOffset.Parse(config["Masterdata:Create:UntilUtc"]!,CultureInfo.InvariantCulture),true,true,proven.SignatureVerified,
            true,proven.AbsenceHttpStatus==404,true,LeaseValid());
        LastResult=await executePrepared(prepared,prepared.CreateRequest(attempt,identity,approval),proven,ct);
        try{await RefreshHistoryAsync(ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){HistoryReadStatus="MASTERDATA_HISTORY_REFRESH_CANCELLED — resultado preservado";}
        return LastResult;
    }
    sealed class CachedSap(SapBpReadResultR20B data):ISapBpMasterReaderR20B
    {public Task<SapBpReadResultR20B> ReadAsync(string businessPartner,CancellationToken ct)=>Task.FromResult(data);}
    public async ValueTask DisposeAsync()
    {if(disposed)return;disposed=true;prepared?.Dispose();http?.Dispose();if(source is not null)await source.DisposeAsync();tunnel?.Dispose();}
}
