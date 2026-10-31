using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;

internal sealed class AutomaticFixture
{
    public readonly TestVersions Versions = new();
    public readonly Ledger Journal = new();
    public readonly Links H4 = new();
    public readonly TestSoap Soap = new();
    public readonly TestRest Rest = new();
    public static AcmR57NormalOptions Options(bool callback = true) => new()
    {
        Enabled=true, ExecutionAuthorized=true, AuthorizationReference="SYNTHETIC_TEST_NOT_OPERATIONAL_AUTHORIZATION",
        GuardianEnvironment="HML", GuardianIdentityEvidence="SYNTHETIC_GUARDIAN_IDENTITY",
        PostgreSqlIdentityEvidence="SYNTHETIC_DATABASE_IDENTITY", JournalEvidence="SYNTHETIC_MEMORY_LEDGER",
        SapIdentifiersCopiedVerbatimApproved=true, IdentifierMappingEvidence="SYNTHETIC_VERBATIM_MAPPING",
        ValorProximidadeFimContrato=0m, ProximityValueEvidence="SYNTHETIC_VALUE_NOT_BUSINESS_APPROVAL",
        CallbackUrl=callback?"https://callback.example.invalid/receive":null,
        CallbackApproved=callback, CallbackEvidence=callback?"SYNTHETIC_CALLBACK_APPROVAL":null
    };

    public AcmR57Pipeline Pipeline(AcmR57NormalOptions? options = null)
    {
        options ??= Options();
        return new(Versions,new AcmR57NormalPreparation(options),new AcmR57NormalRuntimeGate(options),Soap,Rest,Journal,H4);
    }

    public (VersaoContratoSapToledo Version, ClaimIntegracaoMotor Claim) Add(string number = "1147", Func<ContratoSapToledoSnapshot,ContratoSapToledoSnapshot>? change = null)
    {
        // Only the supplied reference values are business examples. Description/provenance are explicitly synthetic.
        var snapshot = new ContratoSapToledoSnapshot(null,number,"AC3P","SYNTHETIC TEST DESCRIPTION",null,null,null,null,null,
            "T",135000.000m,null,"0020000051","7000000","E",null,null,null,null,null,"3011","10",[]){Item="10"};
        if(change is not null) snapshot=change(snapshot);
        var canonical=new ContratoSapToledoCanonicalizer().Canonicalizar(snapshot);
        var id=Guid.NewGuid();
        var version=new VersaoContratoSapToledo(ResultadoRegistroVersaoContrato.Criada,number,1,canonical.FingerprintSha256,id,canonical.Snapshot);
        var claim=new ClaimIntegracaoMotor(id,Guid.NewGuid(),"SYNTHETIC-"+id.ToString("N"),"SAP","TOLEDO","SAP_CONTRATO_TOLEDO",number,
            canonical.FingerprintSha256,1,"SYNTHETIC_WORKER",DateTimeOffset.UtcNow.AddHours(1));
        Versions.Items[id]=version;
        Journal.Claims[id]=claim;
        Soap.BeforeCreate=()=>
        {
            if(Journal.Results[id].Etapa!="R165U_SOAP_ARMED") throw new InvalidOperationException("TEST_COMMIT_BEFORE_SOAP_REQUIRED");
        };
        Rest.BeforeCreate=()=>
        {
            if(!Journal.Results.Values.Any(x=>x.Etapa=="R165U_REST_ARMED")) throw new InvalidOperationException("TEST_COMMIT_BEFORE_REST_REQUIRED");
        };
        return(version,claim);
    }

    internal sealed class TestVersions : IContratoSapToledoVersaoStore
    {
        public readonly Dictionary<Guid,VersaoContratoSapToledo> Items=new();
        public bool Predecessor;
        public Task<VersaoContratoSapToledo?> ObterPorIntegracaoIdAsync(Guid id,CancellationToken ct)=>Task.FromResult(Items.GetValueOrDefault(id));
        public Task<bool> TemPredecessorNaoTerminalAsync(string number,long version,CancellationToken ct)=>Task.FromResult(Predecessor);
        public Task<VersaoContratoSapToledo> RegistrarOuObterAsync(ContratoSapToledoSnapshot s,string f,string p,string c,CancellationToken ct)=>throw new InvalidOperationException("NO_INBOUND_REPLAY_IN_TEST");
    }
    internal sealed class Ledger : IResultadoFluxoMvpStore
    {
        public readonly Dictionary<Guid,ResultadoFluxoMvp> Results=new();
        public readonly Dictionary<Guid,ClaimIntegracaoMotor> Claims=new();
        public readonly List<ResultadoFluxoMvp> History=new();
        public string? RejectStage;
        public bool ThrowOnCommit;
        public bool Unavailable;
        public Task ExigirDisponibilidadeAsync(CancellationToken ct)=>Unavailable?Task.FromException(new InvalidOperationException("SYNTHETIC_UNAVAILABLE")):Task.CompletedTask;
        public Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor c,CancellationToken ct)=>Task.FromResult(Results.GetValueOrDefault(c.IntegracaoId));
        public Task<bool> PersistirAsync(ClaimIntegracaoMotor c,ResultadoFluxoMvp r,CancellationToken ct)=>throw new InvalidOperationException("FENCED_CAS_ONLY");
        public Task<bool> ReservarEtapaAsync(ClaimIntegracaoMotor c,string? expected,ResultadoFluxoMvp r,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock(Results)
            {
                if(ThrowOnCommit)throw new InvalidOperationException("SYNTHETIC_COMMIT_UNKNOWN");
                if(RejectStage==r.Etapa || Claims.GetValueOrDefault(c.IntegracaoId)!=c || c.LeaseExpiraEm<=DateTimeOffset.UtcNow ||
                    Results.GetValueOrDefault(c.IntegracaoId)?.Etapa!=expected) return Task.FromResult(false);
                Results[c.IntegracaoId]=r.Normalizar();History.Add(r);
                return Task.FromResult(true);
            }
        }
    }
    internal sealed class Links : IAcmGuardianVinculoStore
    {
        public readonly Dictionary<Guid,AcmGuardianVinculo> Items=new();
        public bool FailContractCommit;
        public bool FailProtocolCommit;
        public Task<AcmGuardianVinculo?> ObterPorIntegracaoAsync(Guid id,CancellationToken ct)=>Task.FromResult(Items.GetValueOrDefault(id));
        public Task<AcmGuardianVinculo> ReservarAsync(VersaoContratoSapToledo v,string r,string c,CancellationToken ct)=>ReservarSolicitacaoUnicaAsync(v,r,c,ct);
        public Task<AcmGuardianVinculo> ReservarSolicitacaoUnicaAsync(VersaoContratoSapToledo v,string r,string c,CancellationToken ct)
        {
            lock(Items)
            {
                if(Items.Values.Any(x=>x.NumeroReferenciaSolicitado==r && x.IntegracaoIdOrigem!=v.IntegracaoId))throw new InvalidOperationException("SYNTHETIC_REFERENCE_COLLISION");
                if(!Items.TryGetValue(v.IntegracaoId,out var link))Items[v.IntegracaoId]=link=new(v.NumeroContrato,v.Versao,v.IntegracaoId,r,c,null,null,null,null,null,null);
                return Task.FromResult(link);
            }
        }
        public Task<AcmGuardianVinculo> RegistrarCodigoConfirmadoAsync(Guid id,string code,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(FailContractCommit)throw new InvalidOperationException("SYNTHETIC_IDENTITY_COMMIT_UNKNOWN");
            Items[id]=Items[id] with{CodigoContratoConfirmado=code}; return Task.FromResult(Items[id]);
        }
        public Task<AcmGuardianVinculo> RegistrarProtocoloConfirmadoAsync(Guid id,long protocol,string? ticket,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(FailProtocolCommit)throw new InvalidOperationException("SYNTHETIC_PROTOCOL_COMMIT_UNKNOWN");
            Items[id]=Items[id] with{ProtocoloIdGuardian=protocol,NumeroTicketGuardian=ticket};return Task.FromResult(Items[id]);
        }
        public Task<AcmGuardianVinculo> VincularCallbackComprovadoAsync(Guid id,long callback,long seq,CancellationToken ct)=>throw new InvalidOperationException("CALLBACK_NOT_AUTHORIZED");
        public Task<AcmGuardianVinculo?> ResolverCallbackAsync(long callback,long seq,CancellationToken ct)=>throw new InvalidOperationException("CALLBACK_NOT_AUTHORIZED");
        public Task<StatusEventoAcmGuardian> RegistrarFingerprintCallbackAsync(long callback,long seq,string fp,CancellationToken ct)=>throw new InvalidOperationException("CALLBACK_NOT_AUTHORIZED");
    }
    internal sealed class TestSoap : IGuardianContratoSincronizacaoGateway
    {
        public EstadoConsultaContratoGuardian Lookup=EstadoConsultaContratoGuardian.InexistenteComprovado;
        public ContratoGuardianSet26? Existing;
        public Exception? Failure;
        public string? WrongCode;
        public int Lookups,Creates;
        public ContratoGuardianSet26? Sent;
        public Action? BeforeCreate;
        public Action? AfterResponse;
        public Task<ConsultaContratoGuardian> ConsultarAsync(string code,CancellationToken ct)
        {Lookups++;return Task.FromResult(new ConsultaContratoGuardian(Lookup,Existing,Existing is null?null:900));}
        public Task<ResultadoContratoGuardianComResposta> CadastrarComRespostaAsync(ContratoGuardianSet26 c,CancellationToken ct)
        {
            BeforeCreate?.Invoke();Creates++;Sent=c;AfterResponse?.Invoke();
            return Failure is null?Task.FromResult(new ResultadoContratoGuardianComResposta(WrongCode??c.Codigo,900)):
                Task.FromException<ResultadoContratoGuardianComResposta>(Failure);
        }
        public Task CadastrarAsync(ContratoGuardianSet26 c,CancellationToken ct)=>throw new InvalidOperationException("TYPED_RESPONSE_REQUIRED");
        public Task<ResultadoContratoGuardianComResposta> AlterarComRespostaAsync(ContratoGuardianSet26 a,ContratoGuardianSet26 b,CancellationToken ct)=>throw new InvalidOperationException("NO_UPDATE_AUTHORIZED");
    }
    internal sealed class TestRest : IAcmPreCadastroMinimoGatewayR57
    {
        public int Creates;
        public Exception? Failure;
        public bool WrongIdentity;
        public PreCadastroAcmR57? Sent;
        public Action? BeforeCreate;
        public Action? AfterResponse;
        public Task<ProtocoloAcmR57> EnviarMinimoAcmAsync(PreCadastroAcmR57 r,CancellationToken ct)
        {
            BeforeCreate?.Invoke();Creates++;Sent=r;AfterResponse?.Invoke();
            return Failure is null?Task.FromResult(new ProtocoloAcmR57(800+Creates,WrongIdentity?"WRONG":r.Dados.CodigoContrato,
                r.Dados.CodigoItem,r.Dados.CodigoEmissor,r.Dados.NumeroReferencia,"SYNTHETIC-TICKET")):
                Task.FromException<ProtocoloAcmR57>(Failure);
        }
    }
}
