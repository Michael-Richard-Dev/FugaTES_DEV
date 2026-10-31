using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.Mvp;
using FugaTES.Aplicacao.Servicos.Mvp;
using Xunit;

public sealed class GuardianUnifiedDevIntegrationTests
{
    // Every gateway and record in this file is the existing in-memory/offline fixture.
    [Theory]
    [InlineData("id",false)] [InlineData("sequence",false)] [InlineData("fingerprint",false)]
    [InlineData("id",true)] [InlineData("sequence",true)] [InlineData("fingerprint",true)]
    public async Task Callback_appearing_during_reservation_or_SOAP_blocks_remaining_external_dispatch(string marker,bool duringSoap)
    {
        var f=new AutomaticFixture();var(v,c)=f.Add("990801");
        AcmGuardianVinculo Mark(AcmGuardianVinculo link)=>marker switch {
            "id"=>link with{CallbackIdGuardian=700},"sequence"=>link with{CallbackSequencialGuardian=800},
            _=>link with{CallbackFingerprint=new string('A',64)}};
        var links=new RacingLinks(f.H4,Mark,!duringSoap);
        if(duringSoap)f.Soap.AfterResponse=()=>f.H4.Items[v.IntegracaoId]=Mark(f.H4.Items[v.IntegracaoId]);
        var o=AutomaticFixture.Options();var pipeline=new AcmR57Pipeline(f.Versions,new AcmR57NormalPreparation(o),new AcmR57NormalRuntimeGate(o),f.Soap,f.Rest,f.Journal,links);
        var result=await pipeline.ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,result.Status);
        Assert.Equal(duringSoap?1:0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
        await pipeline.ExecutarAsync(c,default);Assert.Equal(duringSoap?1:0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Missing_or_changed_durable_link_after_SOAP_never_starts_REST(bool missing)
    {
        var f=new AutomaticFixture();var(v,c)=f.Add("990802");
        var links=new RacingLinks(f.H4,x=>x,false){ChangeOnSecondRead=missing?null:"OTHER",DropOnSecondRead=missing};
        var o=AutomaticFixture.Options();var pipeline=new AcmR57Pipeline(f.Versions,new AcmR57NormalPreparation(o),new AcmR57NormalRuntimeGate(o),f.Soap,f.Rest,f.Journal,links);
        var result=await pipeline.ExecutarAsync(c,default);
        Assert.Equal(StatusFluxoMvp.ResultadoIndeterminado,result.Status);Assert.Equal(1,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }
    [Fact] public async Task Second_item_preserves_H4_identity_and_blocks_unsupported_duplicate_reference_instead_of_fabricating_one()
    {
        var f=new AutomaticFixture();var(a,first)=f.Add("990803");
        Assert.Equal(StatusFluxoMvp.AguardandoCallback,(await f.Pipeline().ExecutarAsync(first,default)).Status);
        var(b,second)=f.Add("990803",s=>s with{Item="20"});
        var result=await f.Pipeline().ExecutarAsync(second,default);
        Assert.NotEqual(a.IntegracaoId,b.IntegracaoId);Assert.NotEqual(a.FingerprintSha256,b.FingerprintSha256);
        Assert.Equal("20",b.Snapshot.Item);Assert.Equal("R165U_H4_REFERENCE_RESERVATION_FAILED",result.Erro);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);
        Assert.Equal("OC990803",f.H4.Items[a.IntegracaoId].NumeroReferenciaSolicitado);
    }
    [Fact] public async Task New_version_cannot_overtake_an_unfinished_predecessor()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add("990804",s=>s with{DescricaoTipoContrato="SYNTHETIC NEW VERSION"});
        f.Versions.Items[v.IntegracaoId]=v with{Versao=2};f.Versions.Predecessor=true;
        var result=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal("R165U_PREDECESSOR_PENDING",result.Erro);Assert.Equal(0,f.Soap.Creates);Assert.Equal(0,f.Rest.Creates);
    }
    [Fact] public async Task New_pipeline_instance_after_success_uses_persisted_checkpoint_without_any_second_create()
    {
        var f=new AutomaticFixture();var(v,c)=f.Add("990805");var first=await f.Pipeline().ExecutarAsync(c,default);
        var restarted=await f.Pipeline().ExecutarAsync(c,default);
        Assert.Equal(first,restarted);Assert.Equal("R165U_CONFIRMED",f.Journal.Results[v.IntegracaoId].Etapa);
        Assert.Equal(1,f.Soap.Creates);Assert.Equal(1,f.Rest.Creates);Assert.Single(f.H4.Items);
    }
    sealed class RacingLinks(AutomaticFixture.Links inner,Func<AcmGuardianVinculo,AcmGuardianVinculo> mark,bool onReserve):IAcmGuardianVinculoStore
    {
        int reads;public bool DropOnSecondRead;public string? ChangeOnSecondRead;
        public Task<AcmGuardianVinculo?> ObterPorIntegracaoAsync(Guid id,CancellationToken ct)
        {
            var link=inner.Items.GetValueOrDefault(id);reads++;
            if(reads==2){if(DropOnSecondRead)return Task.FromResult<AcmGuardianVinculo?>(null);if(ChangeOnSecondRead is not null)link=link! with{CodigoContratoConfirmado=ChangeOnSecondRead};}
            return Task.FromResult(link);
        }
        public Task<AcmGuardianVinculo> ReservarAsync(VersaoContratoSapToledo v,string reference,string code,CancellationToken ct)=>ReservarSolicitacaoUnicaAsync(v,reference,code,ct);
        public async Task<AcmGuardianVinculo> ReservarSolicitacaoUnicaAsync(VersaoContratoSapToledo v,string reference,string code,CancellationToken ct){var link=await inner.ReservarSolicitacaoUnicaAsync(v,reference,code,ct);if(onReserve)inner.Items[v.IntegracaoId]=link=mark(link);return link;}
        public Task<AcmGuardianVinculo> RegistrarCodigoConfirmadoAsync(Guid id,string code,CancellationToken ct)=>inner.RegistrarCodigoConfirmadoAsync(id,code,ct);
        public Task<AcmGuardianVinculo> RegistrarProtocoloConfirmadoAsync(Guid id,long protocol,string? ticket,CancellationToken ct)=>inner.RegistrarProtocoloConfirmadoAsync(id,protocol,ticket,ct);
        public Task<AcmGuardianVinculo> VincularCallbackComprovadoAsync(Guid id,long callback,long seq,CancellationToken ct)=>throw new InvalidOperationException("NO_CALLBACK_EXECUTION");
        public Task<AcmGuardianVinculo?> ResolverCallbackAsync(long callback,long seq,CancellationToken ct)=>throw new InvalidOperationException("NO_CALLBACK_EXECUTION");
        public Task<StatusEventoAcmGuardian> RegistrarFingerprintCallbackAsync(long callback,long seq,string fp,CancellationToken ct)=>throw new InvalidOperationException("NO_CALLBACK_EXECUTION");
    }
}
