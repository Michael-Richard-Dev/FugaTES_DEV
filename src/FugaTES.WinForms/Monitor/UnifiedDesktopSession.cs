using FugaTES.Aplicacao.Configuracao;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using FugaTES.Aplicacao.Consultas.Monitor;
using R165AL;
using R165AL.Desktop;

namespace FugaTES.WinForms.Monitor;

public sealed record UnifiedDesktopProfile
{
    public bool PhysicalReadbackEnabled{get;init;}
    public string PlanFile{get;init;}="";
    public Guid IntegrationId{get;init;}
    public string ReadKeyFile{get;init;}="";
    public string ApprovalReference{get;init;}="";
    public LocalReadTunnelOptions? Tunnel{get;init;}
    public bool UnifiedDatabaseReadback{get;init;}
}

// Read-only composition for the main desktop. Does not initialize corporate workers/providers.
public sealed class UnifiedDesktopSession:IDisposable
{
    public const string Release="DEV110-TOLEDO-RECEIPTS-MONITOR-20261028-R1";
    public const string Version="1.2.18";
    private readonly UnifiedDesktopProfile profile;
    private readonly string directory;
    private readonly object readerLock=new();
    private readonly SemaphoreSlim probeGate=new(1,1);
    private readonly CancellationTokenSource closed=new();
    private PhysicalMonitorClient? reader;
    private LocalReadTunnel? tunnel;
    private ManualRetry.RetryCommandDesktopBinding? retryBinding;
    private ManualRetry.AcmManualRetryController? retryController;
    // A porta remota do perfil acompanha o estado de falha para que a mensagem de desconexao
    // mostre o endpoint efetivo (tunel local 18761 -> porta remota declarada) e nao um fixo.
    private PhysicalReadConnection failure;
    private bool disposed;
    public event Action? ConnectionChanged;
    private event Action? RetryChanged;
    public PhysicalReadConnection Connection=>reader?.Connection??failure;
    public string EnvironmentDisplay=>"DEV110 — PostgreSQL DEV / leitura e comando manual controlado";
    public string Summary=>"Monitor ACM / contratos / H4 / journal / adapters SOAP e REST integrados.\r\n"+
        (profile.UnifiedDatabaseReadback?"PostgreSQL físico DEV 5433: contratos, monitor e MasterData. Natureza identificada por registro.\r\n":"Readback R165AL: PostgreSQL físico 5433 / Guardian SIMULADO; disponibilidade conferida na consulta.\r\n")+
        "Transportadoras e telas existentes preservadas; workers e escritas não são ativados nesta entrada.\r\n"+
        "Reenviar em Contratos e Protocolos REST: comando depende de perfil privado dedicado, janela e autorização unitária no servidor.\r\n"+
        "Callback Toledo: recebimento HTTP persistido e monitor JSON somente leitura; correlação/devolutiva conforme estado físico.\r\n"+
        "Nenhum CREATE, POST SAP, migration ou replay automático.";
    public UnifiedDesktopSession(UnifiedDesktopProfile profile,string directory)
    {this.profile=profile;this.directory=directory;failure=new(){RemoteReadPort=profile.Tunnel?.RemoteReadPort??0};}
    public static UnifiedDesktopSession Load(string directory)
    {
        try
        {
            var path=Path.Combine(directory,"FugaTES.unified.json");
            var profile=File.Exists(path)?JsonSerializer.Deserialize<UnifiedDesktopProfile>(File.ReadAllText(path)):null;
            return new(profile??new(),directory);
        }
        catch{return new(new(),directory);}
    }
    public async Task RefreshConnectionAsync()
    {
        if(disposed || !await probeGate.WaitAsync(0))return;
        try{await GetReader().ConsultarAsync(new(),closed.Token);}
        catch(Exception error) when(!disposed)
        {
            if(reader is null){failure=failure.Failure(error is PhysicalConnectionException e?e.Code:"READ_PROFILE_REQUIRED");ConnectionChanged?.Invoke();}
        }
        catch(OperationCanceledException) when(disposed){}
        catch(ObjectDisposedException) when(disposed){}
        catch(Exception) when(disposed){}
        finally{probeGate.Release();}
    }
    private PhysicalMonitorClient GetReader()
    {
        lock(readerLock)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(reader is not null)return reader;
            if(!profile.PhysicalReadbackEnabled || profile.IntegrationId==Guid.Empty || string.IsNullOrWhiteSpace(profile.ApprovalReference)
                || !Path.IsPathFullyQualified(profile.ReadKeyFile))throw new PhysicalConnectionException("READ_PROFILE_REQUIRED");
            if(profile.Tunnel is not {Enabled:true})throw new PhysicalConnectionException("SSH_BINDING_REQUIRED");
            if(profile.Tunnel.RemoteReadPort!=(profile.UnifiedDatabaseReadback?18762:18761))throw new PhysicalConnectionException("SSH_BINDING_REQUIRED");
            PilotPlan plan;
            try{plan=JsonSerializer.Deserialize<PilotPlan>(File.ReadAllText(Path.GetFullPath(profile.PlanFile,directory)),new JsonSerializerOptions(JsonSerializerDefaults.Web))!;plan.Validate();}
            catch{throw new PhysicalConnectionException("READ_PROFILE_REQUIRED");}
            var keyFile=PrivateReadKeyMetadata.Validate(profile.ReadKeyFile);
            var acl=keyFile.GetAccessControl();
            var ownSid=WindowsIdentity.GetCurrent().User!.Value;
            var allowed=new HashSet<string>{ownSid,"S-1-5-18"};
            if(!acl.AreAccessRulesProtected || acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(a=>a.AccessControlType==AccessControlType.Allow && !allowed.Contains(a.IdentityReference.Value)))
                throw new PhysicalConnectionException("READ_KEY_ACL_REJECTED");
            // Only the legitimate private read credential is loaded; no database/Guardian credential is consumed.
            Func<CancellationToken,Task>? prepare=null;
            if(profile.Tunnel is {Enabled:true} settings){LocalReadTunnel.BuildArguments(settings);tunnel=new(settings);prepare=tunnel.EnsureAsync;}
            reader=new(new("http://127.0.0.1:18761"),File.ReadAllText(keyFile.FullName).Trim(),plan,profile.IntegrationId,
                new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false},prepare,profile.UnifiedDatabaseReadback,ReadEnvironment(directory));
            reader.ConnectionChanged+=RelayConnection;
            return reader;
        }
    }
    private static AmbienteBanco ReadEnvironment(string directory)
    {
        var file=Path.Combine(directory,"FugaTES.carrier.json");
        if(!File.Exists(file))return AmbientesBanco.Dev110;
        var config=FugaTES.WinForms.Transportadoras.CarrierDesktopProfile.Load(file);
        return AmbientesBanco.PorNome(FugaTES.WinForms.Mm.MmPurchaseOrderComposition.Ambiente(config).Nome)!;
    }
    private void RelayConnection(){if(!disposed)ConnectionChanged?.Invoke();}
    public FrmMonitorMvp CreateMonitor()
    {
        try
        {
            var current=GetReader();
            var form=new FrmMonitorMvp(new MonitorOperacionalConsulta(current),current.PreviewSource,lerAcm:current.DetalharAcmAsync,
                leituraExportada:true,estadoFonte:()=>current.Connection.Display,protocolClient:current)
                {Text=profile.UnifiedDatabaseReadback?"FugaTES DEV110 — Monitor ACM — POSTGRESQL DEV REAL — SOMENTE LEITURA":"FugaTES DEV110 — Monitor ACM — POSTGRESQL REAL / GUARDIAN SIMULADO — SOMENTE LEITURA"};
            void Refresh(){if(!form.IsDisposed&&form.IsHandleCreated)form.BeginInvoke(async()=>await form.RefreshReadbackAsync());}
            RetryChanged+=Refresh;form.FormClosed+=(_,_)=>RetryChanged-=Refresh;return form;
        }
        catch
        {
            return new(new MonitorOperacionalConsulta(new Unavailable()),"DEV110 — readback físico indisponível; binding privado requerido")
                {Text="FugaTES DEV110 — Monitor ACM — LEITURA FÍSICA NÃO DISPONÍVEL"};
        }
    }
    public FrmContratosAcm CreateContracts()=>CreateContractsScoped(null);
    public FrmContratosAcm CreateContracts(Guid integrationId)=>CreateContractsScoped(integrationId);
    private FrmContratosAcm CreateContractsScoped(Guid? integrationId)
    {
        try{return new(new FugaTES.Aplicacao.Consultas.ContratosAcm.ContratosAcmConsulta(GetReader()),
            manualRetry:GetRetryClient(),
            retryStateChanged:()=>{RetryChanged?.Invoke();_=RefreshConnectionAsync();},integrationScope:integrationId,sharedRetry:GetRetryController())
            {Text="Contratos ACM — PostgreSQL DEV 5433 — JSON por contrato/item — projeção persistida"};}
        catch{return new(new FugaTES.Aplicacao.Consultas.ContratosAcm.ContratosAcmConsulta(new UnavailableContracts()),operationalBlocker:Connection.Display);}
    }
    private FugaTES.Aplicacao.Servicos.ManualRetry.IAcmManualRetryCommandClient GetRetryClient()
    {
        lock(readerLock)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            retryBinding??=ManualRetry.RetryCommandDesktopBinding.Load(directory,profile.ReadKeyFile,GetReader().DetalharAcmAsync);
            return retryBinding.Client;
        }
    }
    private ManualRetry.AcmManualRetryController GetRetryController()
    {
        lock(readerLock){ObjectDisposedException.ThrowIf(disposed,this);return retryController??=new(GetRetryClient());}
    }
    public Guardian.FrmProtocolosPreCadastro CreateProtocols()
    {
        FugaTES.Aplicacao.Consultas.ProtocolosGuardian.IProtocolosPreCadastroReadRepository repository;
        try
        {
            var current=GetReader();
            repository=new Guardian.UnifiedProtocolReadRepository(current,current.DetalharAcmAsync);
        }
        catch { repository=new Guardian.UnifiedProtocolReadRepository(new Unavailable(),(_,_)=>throw new InvalidOperationException("PHYSICAL_READBACK_NOT_CONFIGURED")); }
        var protocols=new Guardian.FrmProtocolosPreCadastro(new FugaTES.Aplicacao.Consultas.ProtocolosGuardian.ProtocolosPreCadastroConsulta(repository),
            abrirContrato:(row,owner)=>{using var form=CreateContracts(row.IntegracaoId);form.Text+=" — contrato "+row.Contrato;form.ShowDialog(owner);})
            {Text="Protocolos de Pré-Cadastro Guardian — PostgreSQL DEV 5433 — leitura GET-only / comando REST dedicado"};
        ManualRetry.ProtocolRestRetryController controller;
        try{controller=new(GetRetryController(),repository,GetReader());}
        catch{controller=new(new(new ManualRetry.ReadOnlyAcmManualRetryClient()),repository,new UnavailableContracts());}
        protocols.ConfigureRestRetry(controller,()=>{RetryChanged?.Invoke();_=RefreshConnectionAsync();});
        void Refresh(){if(!protocols.IsDisposed&&protocols.IsHandleCreated)protocols.BeginInvoke(async()=>await protocols.ConsultarAsync());}
        RetryChanged+=Refresh;protocols.FormClosed+=(_,_)=>RetryChanged-=Refresh;
        return protocols;
    }
    private Mm.MmRetryController? mmRetryController;
    public FrmPedidosCompraMm CreateMm()
    {
        try
        {
            // Keep shared Desktop publications compatible until the explicit MM cutover.
            if(!Mm.MmPurchaseOrderComposition.IsVmObserver(directory))return new FrmPedidosCompraMm();
            var form=new FrmPedidosCompraMm(ct=>GetReader().ReadMmAsync(ct));
            lock(readerLock)mmRetryController??=new(GetRetryClient() as FugaTES.Aplicacao.Modelos.Mm.IMmRetryCommandClient);
            form.ConfigureRetry(mmRetryController,()=>RetryChanged?.Invoke());return form;
        }
        catch{return new FrmPedidosCompraMm(_=>Task.FromException<FugaTES.Aplicacao.Modelos.Mm.MmRemoteReadback>(new InvalidOperationException("MM_HOST_BINDING_INVALID")));}
    }
    public Toledo.FrmRecebimentosToledo CreateToledoReceipts()
    {
        try{var current=GetReader();return new(current,()=>current.Connection.Display,GetRetryClient() as FugaTES.Aplicacao.Servicos.Recebimento.IComandoReenvioToledoSapClient);}
        catch{return new(new UnavailableToledo(),()=>Connection.Display);}
    }
    private sealed class UnavailableToledo:FugaTES.Aplicacao.Consultas.RecebimentosToledo.IRecebimentosToledoReadRepository
    {
        public Task<FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentosToledoSnapshot> ConsultarRecebimentosAsync(string? busca,CancellationToken ct)=>throw new InvalidOperationException("TOLEDO_READ_PROFILE_UNAVAILABLE");
        public Task<FugaTES.Aplicacao.Consultas.RecebimentosToledo.RecebimentoToledoDocumento> ObterJsonRecebidoAsync(Guid id,CancellationToken ct)=>throw new InvalidOperationException("TOLEDO_READ_PROFILE_UNAVAILABLE");
    }
    public Transportadoras.MasterdataR3.FrmTransportadoraMasterdata CreateCarriers()
    {
        try{return new(readOnly:true,readDatabase:GetReader().ReadMasterdataAsync);}
        catch{return new(readOnly:true);}
    }
    private sealed class UnavailableContracts:FugaTES.Aplicacao.Consultas.ContratosAcm.IContratosAcmReadRepository
    {
        public Task<IReadOnlyList<FugaTES.Aplicacao.Consultas.ContratosAcm.ContratoAcmLeitura>> ConsultarAsync(string centro,CancellationToken ct)
            =>throw new InvalidOperationException("UNIFIED_DEV_READ_PROFILE_UNAVAILABLE");
    }
    public void Dispose()
    {
        lock(readerLock)
        {
            if(disposed)return;disposed=true;closed.Cancel();
            if(reader is not null){reader.ConnectionChanged-=RelayConnection;reader.Dispose();}
            retryBinding?.Dispose();
            tunnel?.Dispose();
        }
    }
    private sealed class Unavailable:IMonitorReadRepository
    {
        public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filter,CancellationToken ct)=>throw new InvalidOperationException("PHYSICAL_READBACK_NOT_CONFIGURED");
        public Task<MonitorDetalhe> DetalharAsync(Guid id,CancellationToken ct)=>throw new InvalidOperationException("PHYSICAL_READBACK_NOT_CONFIGURED");
    }
}

