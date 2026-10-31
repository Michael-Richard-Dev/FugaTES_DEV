using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Servicos.Sd;
using Xunit;

namespace FugaTES.Testes;

public sealed class SdDeliveryMvpPlannerTests
{
    private static readonly SdIntentEvidence Durable = new(true, false, false);
    private static SdOrderItem Order(decimal qty=100m,string unit="KG") => new("000010","4000095",qty,unit);
    private static SdRequestedItem Item(decimal qty=30m,string unit="KG",bool biodiesel=false)
        => new("000010","4000095",qty,unit,biodiesel);
    private static SdExistingDelivery Delivery(string doc,decimal qty,string unit="KG")
        => new(doc,[new("000010","4000095",qty,unit)]);
    private static SdDeliveryPlan Plan(
        IReadOnlyList<SdExistingDelivery>? existing=null, SdIntentEvidence? evidence=null,
        bool trigger=true,bool biodieselConfirmed=false,bool biodieselHml=false,
        bool complete=true,IReadOnlyList<SdOrderItem>? order=null,
        IReadOnlyList<SdRequestedItem>? intent=null,string intentId="INT-NEW")
        => SdDeliveryMvpPlanner.Evaluate("SO-NEW",intentId,order??[Order()],intent??[Item()],
            existing??[],complete,evidence??Durable,trigger,biodieselConfirmed,biodieselHml);

    // 10 adapted R2 cases, with no assumption that intention quantity == original OV quantity.
    [Fact] public void KgQuantity() => Assert.Equal(30m,Assert.Single(Plan().Items).IntendedQuantity);
    [Fact] public void Biodiesel1LGuard() => Assert.Equal("SD_BIODIESEL_1L_MANUAL_CONFIRMATION_PENDING",
        Plan(order:[Order(1,"L")],intent:[Item(1,"L",true)]).Reason);
    [Fact] public void RequiredFields() => Assert.Equal("SD_REQUIRED_FIELDS_OR_INTENT_ID",Plan(intent:[Item(0)]).Reason);
    [Fact] public void ExistingDelivery() => Assert.Equal(SdDeliveryPlanState.RemessaExistente,
        Plan([Delivery("D1",30)],new(true,true,false,"D1")).State);
    [Fact] public void PartialDelivery() {
        var p=Plan([Delivery("D1",40)]);
        Assert.Equal(SdDeliveryPlanState.RemessaParcial,p.State);
        Assert.Equal(60m,Assert.Single(p.Items).StillEligible);
        Assert.Equal(30m,Assert.Single(p.Items).IntendedQuantity);
    }
    [Fact] public void DeliverySplit() {
        var p=Plan([Delivery("D1",20),Delivery("D2",30)]);
        Assert.Equal(SdDeliveryPlanState.RemessaParcial,p.State);
        Assert.Equal(50m,Assert.Single(p.Items).StillEligible);
        Assert.Equal(2,p.ExistingDocuments.Count);
    }
    [Fact] public void IdempotencyLookupGuard() => Assert.Equal("SD_DELIVERY_LOOKUP_INCOMPLETE",Plan(complete:false).Reason);
    [Fact] public void TimeoutIndeterminate() => Assert.Equal(SdDeliveryPlanState.ResultadoIndeterminado,
        Plan(evidence:new(true,false,true)).State);
    [Fact] public void ManualCompletionPending() => Assert.Equal("SD_TRIGGER_PENDING",Plan(trigger:false).Reason);
    [Fact] public void UnitMismatch() => Assert.Equal("SD_EXISTING_DELIVERY_MATERIAL_OR_UNIT_MISMATCH",
        Plan([Delivery("D1",10,"L")]).Reason);

    // R3 focal cases.
    [Fact] public void KG_SEM_GUARD_BIODIESEL() => Assert.Equal(SdDeliveryPlanState.ProntaParaValidacao,
        Plan(biodieselConfirmed:false,biodieselHml:false).State);
    [Fact] public void BIODIESEL_1L_PLANEJAMENTO() => Assert.Equal(SdDeliveryPlanState.ProntaParaValidacao,
        Plan(order:[Order(1,"L")],intent:[Item(1,"L",true)],biodieselConfirmed:true,biodieselHml:true).State);
    [Fact] public void BIODIESEL_HML_NAO_VALIDADO() => Assert.Equal("SD_BIODIESEL_1L_HML_NOT_VALIDATED",
        Plan(order:[Order(1,"L")],intent:[Item(1,"L",true)],biodieselConfirmed:true,biodieselHml:false).Reason);
    [Fact] public void INTENCAO_LOGISTICA_DUPLICADA() => Assert.Equal(SdDeliveryPlanState.ResultadoIndeterminado,
        Plan(evidence:new(true,true,false,"DOC_UNKNOWN")).State);
    [Fact] public void REMESSA_PARCIAL() => Assert.Equal(SdDeliveryPlanState.RemessaParcial,Plan([Delivery("D1",15)]).State);
    [Fact] public void DELIVERY_SPLIT() => Assert.Equal(2,Plan([Delivery("D1",10),Delivery("D2",10)]).ExistingDocuments.Count);
    [Fact] public void TIMEOUT_SEM_REPOST() => Assert.False(Plan(evidence:new(true,false,true)).CanIssueSapWrite);
    [Fact] public void GUARDIAN_NOT_CALLED() => Assert.False(Plan().CanIssueSapWrite);
    [Fact] public void IntentionExceedsEligible() => Assert.Equal("SD_INTENTION_EXCEEDS_ELIGIBLE_QUANTITY",
        Plan([Delivery("D1",80)],intent:[Item(30)]).Reason);
    [Fact] public void IntentIdentityNotDurable() => Assert.Equal("SD_DURABLE_INTENTION_NOT_PROVEN_NO_POST",
        Plan(evidence:new(false,false,false)).Reason);
    [Fact] public void NoAutomaticCompletion() => Assert.NotEqual(SdDeliveryPlanState.Concluida,Plan().State);
    [Fact] public void KGReadyCannotPost() => Assert.False(Plan().SapPostEnabled);
    [Fact] public void DeliveryPastOrderBlocked() => Assert.Equal("SD_DELIVERED_EXCEEDS_SALES_ORDER",
        Plan([Delivery("D1",101)]).Reason);
    [Fact] public void BiodieselOtherVolumeBlocked() => Assert.Equal("SD_UNIT_NOT_AUTHORIZED_FOR_PLANNING",
        Plan(order:[Order(2,"L")],intent:[Item(2,"L",true)],biodieselConfirmed:true,biodieselHml:true).Reason);
}

public sealed class SdDeliveryReadOnlyReviewServiceTests
{
    private sealed class FakeOv : IConsultaOrdemVendaSap
    {
        public int Reads;
        public Task<ResultadoConsultaSap<SalesOrderSnapshot>> ConsultarAsync(string so,CancellationToken ct)
        {
            Reads++;
            var snap=new SalesOrderSnapshot(so,null,null,null,null,
                [new SalesOrderItemSnapshot("000010","4000095",100m,"KG","3008")]);
            return Task.FromResult(ResultadoConsultaSap<SalesOrderSnapshot>.Sucesso(snap));
        }
    }
    private sealed class FakeDelivery : IRemessaSap
    {
        public int Reads;public int Writes;
        public Task<ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>> ObterCandidatasPorOrdemVendaAsync(string so,CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsultaSap.NaoEncontrado));
        }
        public Task<ResultadoConsultaSap<DeliverySnapshot>> ObterPorOrdemVendaAsync(string so,CancellationToken ct)
            => throw new InvalidOperationException("NOT_USED");
        public Task<ResultadoEscritaSap<DeliverySnapshot>> CriarAsync(CriarRemessaComando command,CancellationToken ct)
        { Writes++;throw new InvalidOperationException("NO_SAP_POST_ALLOWED"); }
        public Task<ResultadoEscritaSap<DeliverySnapshot>> AtualizarAsync(string doc,string etag,IReadOnlyDictionary<string,object?> changes,CancellationToken ct)
        { Writes++;throw new InvalidOperationException("NO_SAP_PATCH_ALLOWED"); }
    }
    [Fact] public async Task SapGetsOnly_ZeroSapPost_ZeroGuardianDependency()
    {
        var so=new FakeOv();var deliveries=new FakeDelivery();
        var service=new SdDeliveryReadOnlyReviewService(so,deliveries);
        var plan=await service.ReviewAsync("SO-NEW","INT-NEW",[new("000010","4000095",30m,"KG")],
            new(true,false,false),true,false,false,CancellationToken.None);
        Assert.Equal(SdDeliveryPlanState.ProntaParaValidacao,plan.State);
        Assert.Equal(1,so.Reads);Assert.Equal(1,deliveries.Reads);Assert.Equal(0,deliveries.Writes);
        Assert.False(plan.CanIssueSapWrite);
    }
    [Fact] public void WinFormsProjectionExposesEightFields()
    {
        var plan=SdDeliveryMvpPlanner.Evaluate("SO","I",[new("10","MAT",10m,"KG")],
            [new("10","MAT",3m,"KG")],[],true,new(true,false,false),true,false,false);
        var row=Assert.Single(SdDeliveryReviewProjection.Rows(plan));
        Assert.Equal("SO",row.OV); Assert.Equal("10",row.ITEM);Assert.Equal("MAT",row.MATERIAL);
        Assert.Equal("KG",row.UNIDADE);Assert.Equal(3m,row.QUANTIDADE_PLANEJADA);
        Assert.Equal("",row.REMESSA_SAP);Assert.Equal("PRONTA_PARA_VALIDACAO",row.STATUS);
        Assert.Equal("SD_READY_FOR_OFFLINE_REVIEW_ONLY",row.MOTIVO_BLOQUEIO);
    }
}
