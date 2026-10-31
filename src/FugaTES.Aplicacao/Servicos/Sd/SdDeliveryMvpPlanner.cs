using System;
using System.Collections.Generic;
using System.Linq;

namespace FugaTES.Aplicacao.Servicos.Sd;

// Offline-only logistics assessment. This component cannot issue SAP/Guardian writes.
public enum SdDeliveryPlanState
{
    ProntaParaValidacao,
    RemessaExistente,
    RemessaParcial,
    PendenteAjusteManual,
    ResultadoIndeterminado,
    Concluida // Reserved; never emitted by the planning-only path.
}

public sealed record SdOrderItem(string Item, string Material, decimal Quantity, string Unit);
public sealed record SdRequestedItem(string Item, string Material, decimal Quantity, string Unit, bool IsBiodiesel = false);
public sealed record SdExistingItem(string SalesOrderItem, string Material, decimal Quantity, string Unit);
public sealed record SdExistingDelivery(string DeliveryDocument, IReadOnlyList<SdExistingItem> Items);

// Proof must originate in an approved durable repository; a UI flag is not proof.
public sealed record SdIntentEvidence(
    bool DurableIdentityVerified,
    bool DuplicateIntent,
    bool PriorAttemptIndeterminate,
    string? ExistingDocumentForIntent = null);

public sealed record SdDeliveryItemPlan(
    string Item,
    string Material,
    string Unit,
    decimal SalesOrderQuantity,
    decimal AlreadyDelivered,
    decimal StillEligible,
    decimal IntendedQuantity);

public sealed record SdDeliveryPlan(
    SdDeliveryPlanState State,
    string Reason,
    string SalesOrder,
    string IntentId,
    IReadOnlyList<SdDeliveryItemPlan> Items,
    IReadOnlyList<string> ExistingDocuments,
    bool SapPostEnabled = false)
{
    public bool CanIssueSapWrite => false; // Explicit, non-configurable in R3.
}

public static class SdDeliveryMvpPlanner
{
    public static SdDeliveryPlan Evaluate(
        string salesOrder,
        string intentId,
        IReadOnlyList<SdOrderItem> salesOrderItems,
        IReadOnlyList<SdRequestedItem> intendedItems,
        IReadOnlyList<SdExistingDelivery> existingDeliveries,
        bool lookupComplete,
        SdIntentEvidence evidence,
        bool functionalTriggerApproved,
        bool manualBiodieselOneLitreConfirmed,
        bool biodieselSapHmlValidated)
    {
        ArgumentNullException.ThrowIfNull(salesOrderItems);
        ArgumentNullException.ThrowIfNull(intendedItems);
        ArgumentNullException.ThrowIfNull(existingDeliveries);
        ArgumentNullException.ThrowIfNull(evidence);
        SdDeliveryPlan Block(string reason, SdDeliveryPlanState status = SdDeliveryPlanState.PendenteAjusteManual,
            IReadOnlyList<SdDeliveryItemPlan>? items = null, IReadOnlyList<string>? documents = null)
            => new(status, reason, salesOrder ?? string.Empty, intentId ?? string.Empty,
                items ?? Array.Empty<SdDeliveryItemPlan>(), documents ?? Array.Empty<string>());

        if (string.IsNullOrWhiteSpace(salesOrder) || string.IsNullOrWhiteSpace(intentId) ||
            salesOrderItems.Count == 0 || intendedItems.Count == 0 ||
            !Unique(salesOrderItems.Select(i => i.Item)) || !Unique(intendedItems.Select(i => i.Item)) ||
            salesOrderItems.Any(i => !Valid(i.Item, i.Material, i.Unit, i.Quantity)) ||
            intendedItems.Any(i => !Valid(i.Item, i.Material, i.Unit, i.Quantity)))
            return Block("SD_REQUIRED_FIELDS_OR_INTENT_ID");

        if (!lookupComplete) return Block("SD_DELIVERY_LOOKUP_INCOMPLETE");
        if (existingDeliveries.Any(d => string.IsNullOrWhiteSpace(d.DeliveryDocument) || d.Items is null ||
            d.Items.Any(i => !Valid(i.SalesOrderItem, i.Material, i.Unit, i.Quantity, allowZero: false))) ||
            !Unique(existingDeliveries.Select(d => d.DeliveryDocument)))
            return Block("SD_EXISTING_DELIVERY_INVALID");

        var documents = existingDeliveries.Select(d => d.DeliveryDocument).ToArray();
        if (evidence.PriorAttemptIndeterminate)
            return Block("SD_PREVIOUS_ATTEMPT_INDETERMINATE_NO_REPOST", SdDeliveryPlanState.ResultadoIndeterminado,
                documents: documents);

        var linesByItem = salesOrderItems.ToDictionary(i => i.Item, StringComparer.Ordinal);
        var deliveredByItem = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var delivered in existingDeliveries.SelectMany(d => d.Items))
        {
            if (!linesByItem.TryGetValue(delivered.SalesOrderItem, out var source) ||
                delivered.Material != source.Material || delivered.Unit != source.Unit)
                return Block("SD_EXISTING_DELIVERY_MATERIAL_OR_UNIT_MISMATCH", documents: documents);
            deliveredByItem.TryGetValue(delivered.SalesOrderItem, out var before);
            deliveredByItem[delivered.SalesOrderItem] = before + delivered.Quantity;
        }

        if (salesOrderItems.Any(item =>
            deliveredByItem.GetValueOrDefault(item.Item) > item.Quantity))
            return Block("SD_DELIVERED_EXCEEDS_SALES_ORDER", documents: documents);

        var lines = new List<SdDeliveryItemPlan>();
        foreach (var intended in intendedItems)
        {
            if (!linesByItem.TryGetValue(intended.Item, out var order) ||
                order.Material != intended.Material || order.Unit != intended.Unit)
                return Block("SD_INTENTION_MATERIAL_OR_UNIT_MISMATCH", documents: documents);
            var delivered = deliveredByItem.GetValueOrDefault(intended.Item);
            var eligible = order.Quantity - delivered;
            if (!evidence.DuplicateIntent && intended.Quantity > eligible)
                return Block("SD_INTENTION_EXCEEDS_ELIGIBLE_QUANTITY", documents: documents);
            lines.Add(new(intended.Item, intended.Material, intended.Unit, order.Quantity,
                delivered, eligible, intended.Quantity));
        }

        if (evidence.DuplicateIntent)
        {
            if (string.IsNullOrWhiteSpace(evidence.ExistingDocumentForIntent) ||
                !documents.Contains(evidence.ExistingDocumentForIntent, StringComparer.Ordinal))
                return Block("SD_DUPLICATE_INTENT_DOCUMENT_NOT_RECONCILED",
                    SdDeliveryPlanState.ResultadoIndeterminado, lines, documents);
            return Block("SD_EXISTING_INTENT_RECONCILED_NO_REPOST",
                SdDeliveryPlanState.RemessaExistente, lines, documents);
        }

        // Existing documents on an OV do not automatically belong to this new intent.
        // Show partial/split, require manual logistics decision, never auto-post.
        if (documents.Length > 0)
            return Block("SD_PARTIAL_OR_SPLIT_MANUAL_RECONCILIATION",
                SdDeliveryPlanState.RemessaParcial, lines, documents);

        // Only a durable and verified identity can protect a future write from duplicate intent.
        if (!evidence.DurableIdentityVerified)
            return Block("SD_DURABLE_INTENTION_NOT_PROVEN_NO_POST", items: lines, documents: documents);

        foreach (var intended in intendedItems)
        {
            if (intended.Unit == "KG") continue; // The Biodiesel 1 L rule NEVER gates KG.
            if (!(intended.IsBiodiesel && intended.Unit == "L" && intended.Quantity == 1m))
                return Block("SD_UNIT_NOT_AUTHORIZED_FOR_PLANNING", items: lines);
            if (!manualBiodieselOneLitreConfirmed)
                return Block("SD_BIODIESEL_1L_MANUAL_CONFIRMATION_PENDING", items: lines);
            if (!biodieselSapHmlValidated)
                return Block("SD_BIODIESEL_1L_HML_NOT_VALIDATED", items: lines);
        }

        if (!functionalTriggerApproved)
            return Block("SD_TRIGGER_PENDING", items: lines);

        // "Ready" is a read-only review state, not SAP POST/PGI/billing authorization.
        return new(SdDeliveryPlanState.ProntaParaValidacao, "SD_READY_FOR_OFFLINE_REVIEW_ONLY",
            salesOrder, intentId, lines, documents);
    }

    private static bool Unique(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return values.All(value => !string.IsNullOrWhiteSpace(value) && seen.Add(value));
    }

    private static bool Valid(string id, string material, string unit, decimal quantity, bool allowZero = false)
        => !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(material) &&
           !string.IsNullOrWhiteSpace(unit) && (allowZero ? quantity >= 0m : quantity > 0m);
}
