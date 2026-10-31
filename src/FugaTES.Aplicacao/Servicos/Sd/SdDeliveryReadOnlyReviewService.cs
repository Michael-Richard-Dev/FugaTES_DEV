using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;

namespace FugaTES.Aplicacao.Servicos.Sd;

// Separate from ProcessarSaidaSd; contains no Guardian dependency or SAP write method.
public sealed class SdDeliveryReadOnlyReviewService(
    IConsultaOrdemVendaSap salesOrders,
    IRemessaSap deliveries)
{
    public async Task<SdDeliveryPlan> ReviewAsync(
        string salesOrder, string intentId, IReadOnlyList<SdRequestedItem> intendedItems,
        SdIntentEvidence evidence, bool functionalTriggerApproved,
        bool manualBiodieselOneLitreConfirmed, bool biodieselSapHmlValidated,
        CancellationToken ct)
    {
        SdDeliveryPlan Block(string reason) => new(SdDeliveryPlanState.PendenteAjusteManual,
            reason, salesOrder ?? string.Empty, intentId ?? string.Empty,
            Array.Empty<SdDeliveryItemPlan>(), Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(salesOrder) || string.IsNullOrWhiteSpace(intentId) ||
            intendedItems is null || intendedItems.Count == 0)
            return Block("SD_REQUIRED_FIELDS_OR_INTENT_ID");

        var ov = await salesOrders.ConsultarAsync(salesOrder, ct).ConfigureAwait(false);
        if (ov.Status != StatusConsultaSap.Sucesso || ov.Dados is null)
            return Block("SD_SALES_ORDER_GET_UNAVAILABLE");
        if (ov.Dados.SalesOrder != salesOrder)
            return Block("SD_SALES_ORDER_IDENTITY_MISMATCH");
        if (ov.Dados.Items is null)
            return Block("SD_SALES_ORDER_ITEMS_MISSING");

        var lines = new List<SdOrderItem>();
        foreach (SalesOrderItemSnapshot item in ov.Dados.Items)
        {
            if (item.RequestedQuantity is null || item.Material is null ||
                item.RequestedQuantityUnit is null)
                return Block("SD_SALES_ORDER_ITEM_INCOMPLETE");
            lines.Add(new(item.SalesOrderItem, item.Material,
                item.RequestedQuantity.Value, item.RequestedQuantityUnit));
        }

        var linked = await deliveries.ObterCandidatasPorOrdemVendaAsync(salesOrder, ct).ConfigureAwait(false);
        IReadOnlyList<DeliverySnapshot> candidates;
        if (linked.Status == StatusConsultaSap.NaoEncontrado)
            candidates = Array.Empty<DeliverySnapshot>();
        else if (linked.Status == StatusConsultaSap.Sucesso && linked.Dados is not null)
            candidates = linked.Dados;
        else
            return Block("SD_DELIVERY_LOOKUP_UNAVAILABLE");

        var current = new List<SdExistingDelivery>();
        foreach (var snapshot in candidates)
        {
            if (snapshot.Items is null)
                return Block("SD_DELIVERY_ITEMS_MISSING");
            var currentItems = new List<SdExistingItem>();
            foreach (var item in snapshot.Items)
            {
                if (item.ReferenceSDDocument != salesOrder || item.ReferenceSDDocumentItem is null ||
                    item.Material is null || item.ActualDeliveryQuantity is null ||
                    item.DeliveryQuantityUnit is null)
                    return Block("SD_DELIVERY_ITEM_INCOMPLETE_OR_WRONG_OV");
                currentItems.Add(new(item.ReferenceSDDocumentItem, item.Material,
                    item.ActualDeliveryQuantity.Value, item.DeliveryQuantityUnit));
            }
            current.Add(new(snapshot.DeliveryDocument, currentItems));
        }

        return SdDeliveryMvpPlanner.Evaluate(salesOrder, intentId, lines, intendedItems, current,
            lookupComplete: true, evidence, functionalTriggerApproved,
            manualBiodieselOneLitreConfirmed, biodieselSapHmlValidated);
    }
}

// WinForms can bind these field names without invoking Guardian or SAP writes.
public sealed record SdDeliveryReviewRow(string OV, string ITEM, string MATERIAL,
    string UNIDADE, decimal QUANTIDADE_PLANEJADA, string REMESSA_SAP,
    string STATUS, string MOTIVO_BLOQUEIO);

public static class SdDeliveryReviewProjection
{
    public static IReadOnlyList<SdDeliveryReviewRow> Rows(SdDeliveryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Items.Select(line => new SdDeliveryReviewRow(
            plan.SalesOrder, line.Item, line.Material, line.Unit, line.IntendedQuantity,
            string.Join(",", plan.ExistingDocuments),
            plan.State switch
            {
                SdDeliveryPlanState.ProntaParaValidacao => "PRONTA_PARA_VALIDACAO",
                SdDeliveryPlanState.RemessaExistente => "REMESSA_EXISTENTE",
                SdDeliveryPlanState.RemessaParcial => "REMESSA_PARCIAL",
                SdDeliveryPlanState.PendenteAjusteManual => "PENDENTE_AJUSTE_MANUAL",
                SdDeliveryPlanState.ResultadoIndeterminado => "RESULTADO_INDETERMINADO",
                _ => "CONCLUIDA"
            }, plan.Reason)).ToArray();
    }
}
