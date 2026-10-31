using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.Mvp;
using Xunit;

public class EvidenceDurabilityTests
{
    internal static (CorrelacaoRecebimentoMvp,ClaimIntegracaoMotor,AcmSapTicketEvidence) Fixture()
    {
        var cb=new CallbackGuardianMvp("9471","8810",150,50,100,"5");var fp=CallbackGuardianMvpFingerprint.Calcular(cb);
        var input=new RecebimentoSapCpiMvp("RECEBIMENTO","SYNTHETIC","7000000","0020000184","","0000001106","OC1106","AC3P","3011","10","AC3P",null,10,"0000001106");
        var c=new CorrelacaoRecebimentoMvp("OC1106","9471",input,"8810",cb,EstadoCorrelacaoGuardianSap.TicketAssociado,fp,null,new(150,50,100));
        var e=new AcmSapTicketEvidence("SYNTHETIC_EVIDENCE","9471","8810",fp,"0000001106","3011","7000000","0020000184","PJ",null,"S",new DateTimeOffset(2026,10,22,12,0,0,TimeSpan.Zero),[new("UMIDADE",null,1.2m)],TipoOperacaoSap:7,TipoOperacaoSapProveniencia:"TICKET_BUSINESS_FIELD_ATTESTED",TipoOperacaoSapEvidenceReference:"SYNTHETIC_RULE");
        var claim=new ClaimIntegracaoMotor(Guid.NewGuid(),Guid.NewGuid(),c.DocumentoSap,"GUARDIAN","SAP",GuardianPesagemSapMotorWork.Operacao,"9471",GuardianPesagemSapMotorWork.Fingerprint("9471",fp),1,"SYNTHETIC",DateTimeOffset.UtcNow.AddMinutes(1));
        return(c,claim,e);
    }
    [Fact] public async Task Missing_durable_evidence_store_must_block_before_sap()
    {
        var(c,claim,e)=Fixture();var sap=new Sap();var r=await new ProcessadorPesagemSapMotor(new Correlations(c),sap,preflight:new Guard(),ticketEvidenceSource:new Source(e)).ProcessarAsync(claim,default);
        Assert.Equal(0,sap.Calls);Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva,r.Falha);
    }
    internal sealed class Guard:IAcmR106SapReturnPreflight{public Task<bool> ValidarAsync(CorrelacaoRecebimentoMvp c,CancellationToken ct)=>Task.FromResult(true);}
    internal sealed class Source(AcmSapTicketEvidence e):IAcmSapTicketEvidenceSource{public Task<AcmSapTicketEvidence?> ObterAsync(CorrelacaoRecebimentoMvp c,CancellationToken ct)=>Task.FromResult<AcmSapTicketEvidence?>(e);}
    internal sealed class Sap:IPesagemSapMvpGateway
    {public int Calls;public Action? BeforeSend;public bool Ambiguous;public Task EnviarAsync(PesagemSapMvp p,CancellationToken ct){BeforeSend?.Invoke();Calls++;if(Ambiguous)throw new FalhaPesagemSapMvpException("SYNTHETIC",ClassificacaoFalhaIntegracao.ResultadoIndeterminado);return Task.CompletedTask;}}
    static PesagemSapMvp Input(AcmSapTicketEvidence e)=>new("Toledo","3011","S","8810",null,"SYNTHETIC","7000000","0020000184",150,50,100,"AC3P",null,"OC1106",7,"0000001106","9471",EvidenciaTicket:e);
    [Fact] public async Task Store_commit_precedes_the_only_sap_dispatch()
    {
        var(c,claim,e)=Fixture();var store=new MemoryEvidenceStore();var sap=new Sap{BeforeSend=()=>Assert.True(store.Committed)};
        await new ProcessadorPesagemSapMotor(new Correlations(c),sap,preflight:new Guard(),ticketEvidenceSource:new Source(e),ticketEvidenceStore:store).ProcessarAsync(claim,default);
        Assert.Equal(1,store.Calls);Assert.Equal(1,sap.Calls);Assert.NotNull(store.Snapshot);
    }
    [Fact] public async Task Store_failure_prevents_all_sap_calls()
    {
        var(c,claim,e)=Fixture();var store=new MemoryEvidenceStore{Failure=true};var sap=new Sap();
        var r=await new ProcessadorPesagemSapMotor(new Correlations(c),sap,preflight:new Guard(),ticketEvidenceSource:new Source(e),ticketEvidenceStore:store).ProcessarAsync(claim,default);
        Assert.Equal(0,sap.Calls);Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva,r.Falha);
    }
    [Fact] public async Task Audit_snapshot_remains_available_after_ambiguous_sap_without_retry()
    {
        var(c,claim,e)=Fixture();var store=new MemoryEvidenceStore();var sap=new Sap{Ambiguous=true};
        var r=await new ProcessadorPesagemSapMotor(new Correlations(c),sap,preflight:new Guard(),ticketEvidenceSource:new Source(e),ticketEvidenceStore:store).ProcessarAsync(claim,default);
        Assert.Equal(1,sap.Calls);Assert.Equal(ClassificacaoFalhaIntegracao.ResultadoIndeterminado,r.Falha);Assert.True(store.Committed);
    }
    [Fact] public void Snapshot_is_stable_and_preserves_the_enriched_business_fields()
    {
        var(c,_,e)=Fixture();e=e with{NomeMotorista="SYNTHETIC_DRIVER",BusinessPartnerPropriedade="SYNTHETIC",IdAgendamentoSap="0000000027"};var p=Input(e);
        var first=EvidenciaPesagemGuardianSnapshot.Criar(c,p);var second=EvidenciaPesagemGuardianSnapshot.Criar(c,p);
        Assert.Equal(first,second);Assert.Equal(64,first.Sha256.Length);Assert.Contains("SYNTHETIC_DRIVER",first.Json);Assert.Contains("0000000027",first.Json);Assert.Contains("SYNTHETIC_RULE",first.Json);
        Assert.True(first.ConfirmarExistente(first.Json,first.Sha256,true));Assert.False(first.ConfirmarExistente(null,null,false));
    }
    [Theory][InlineData("json")][InlineData("hash")][InlineData("timestamp")][InlineData("partial")]
    public void Altered_or_incomplete_existing_snapshots_are_not_overwritten(string defect)
    {
        var(c,_,e)=Fixture();var s=EvidenciaPesagemGuardianSnapshot.Criar(c,Input(e));
        Assert.Throws<InvalidOperationException>(()=>s.ConfirmarExistente(defect=="json"?"OTHER":defect=="partial"?null:s.Json,defect=="hash"?new string('0',64):s.Sha256,defect!="timestamp"));
    }
    [Theory][InlineData("ticket")][InlineData("weights")][InlineData("fingerprint")][InlineData("contract")]
    public void Mismatched_snapshot_cannot_be_built(string defect)
    {
        var(c,_,e)=Fixture();var p=Input(e);
        p=defect switch{"ticket"=>p with{IdPesagem="OTHER"},"weights"=>p with{PesoTara=49},"contract"=>p with{ContratoACM="OTHER"},_=>p with{EvidenciaTicket=e with{CallbackFingerprint="OTHER"}}};
        Assert.Throws<InvalidOperationException>(()=>EvidenciaPesagemGuardianSnapshot.Criar(c,p));
    }
    [Fact] public async Task A_changed_soap_snapshot_blocks_sap_instead_of_replacing_prior_evidence()
    {
        var(c,claim,e)=Fixture();var store=new MemoryEvidenceStore();var firstSap=new Sap();
        await new ProcessadorPesagemSapMotor(new Correlations(c),firstSap,preflight:new Guard(),ticketEvidenceSource:new Source(e),ticketEvidenceStore:store).ProcessarAsync(claim,default);
        Assert.Equal(1,firstSap.Calls);
        var sap=new Sap();var changed=e with{NomeMotorista="CHANGED_SYNTHETIC_DRIVER"};
        var r=await new ProcessadorPesagemSapMotor(new Correlations(c),sap,preflight:new Guard(),ticketEvidenceSource:new Source(changed),ticketEvidenceStore:store).ProcessarAsync(claim,default);
        Assert.Equal(0,sap.Calls);Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva,r.Falha);
    }
    [Theory][InlineData("provenance")][InlineData("reference")]
    public void Missing_operational_business_provenance_is_not_a_preservable_evidence(string field)
    {
        var(c,_,e)=Fixture();e=field=="provenance"?e with{TipoOperacaoSapProveniencia=null}:e with{TipoOperacaoSapEvidenceReference=null};
        Assert.Throws<InvalidOperationException>(()=>EvidenciaPesagemGuardianSnapshot.Criar(c,Input(e)));
    }
    [Fact] public async Task Asynchronous_commit_is_awaited_before_sap()
    {
        var(c,claim,e)=Fixture();var store=new DelayedEvidenceStore();var sap=new Sap();
        var pending=new ProcessadorPesagemSapMotor(new Correlations(c),sap,preflight:new Guard(),ticketEvidenceSource:new Source(e),ticketEvidenceStore:store).ProcessarAsync(claim,default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(0,sap.Calls);Assert.False(pending.IsCompleted);
        store.Release.SetResult();await pending;Assert.Equal(1,sap.Calls);
    }
    sealed class DelayedEvidenceStore:IAcmSapTicketEvidenceStore
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PreservarAsync(CorrelacaoRecebimentoMvp c,PesagemSapMvp p,CancellationToken ct)
        {Entered.SetResult();await Release.Task.WaitAsync(ct);_ = EvidenciaPesagemGuardianSnapshot.Criar(c,p);}
    }
    internal sealed class MemoryEvidenceStore:IAcmSapTicketEvidenceStore
    {
        public int Calls;public bool Failure,Committed;public EvidenciaPesagemGuardianSnapshot? Snapshot;
        public Task PreservarAsync(CorrelacaoRecebimentoMvp c,PesagemSapMvp p,CancellationToken ct)
        {
            Calls++;if(Failure)throw new IOException("SYNTHETIC_STORE_FAILURE");var next=EvidenciaPesagemGuardianSnapshot.Criar(c,p);
            if(Snapshot is not null)next.ConfirmarExistente(Snapshot.Json,Snapshot.Sha256,true);
            Snapshot=next;Committed=true;return Task.CompletedTask;
        }
    }
    internal sealed class Correlations(CorrelacaoRecebimentoMvp c):IRepositorioCorrelacaoRecebimento
    {
        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string id,CancellationToken ct)=>Task.FromResult<CorrelacaoRecebimentoMvp?>(c);
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp e,CancellationToken ct)=>throw new NotSupportedException();
        public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string id,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string d,string a,CancellationToken ct)=>throw new NotSupportedException();
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string d,string p,CancellationToken ct,DateTimeOffset at)=>throw new NotSupportedException();
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp cb,CancellationToken ct)=>throw new NotSupportedException();
    }
}
