namespace FugaTES.Aplicacao.Modelos.SAP;

// Modelos aplicacionais normalizados, independentes de OData. Nenhum DTO OData atravessa esta fronteira.
// Gate MVP SAP capabilities SD/MM/ACM R1.

// ---------- SD ----------

public sealed record SalesOrderSnapshot(
    string SalesOrder,
    string? SalesOrderType,
    string? SoldToParty,
    string? SalesOrganization,
    string? OverallDeliveryStatus,
    IReadOnlyList<SalesOrderItemSnapshot> Items,
    IReadOnlyList<SalesOrderPartnerSnapshot>? Partners = null,
    bool? PossuiContratoAcm = null);

public sealed record SalesOrderPartnerSnapshot(string PartnerFunction, string BusinessPartner);

public sealed record SalesOrderItemSnapshot(
    string SalesOrderItem,
    string? Material,
    decimal? RequestedQuantity,
    string? RequestedQuantityUnit,
    string? Plant);

public sealed record PartnerSnapshot(
    string BusinessPartner,
    string? Name,
    int? Category,
    IReadOnlyList<PartnerTaxSnapshot> TaxNumbers,
    bool Blocked,
    IReadOnlyList<string>? Roles = null);

public sealed record PartnerTaxSnapshot(string TaxType, string TaxNumber);

public sealed record ProductSnapshot(
    string Product,
    string? ProductType,
    string? BaseUnit,
    string? ProductGroup,
    string? Description);

public sealed record DeliverySnapshot(
    string DeliveryDocument,
    string? DeliveryDocumentType,
    string? ShippingPoint,
    string? OverallGoodsMovementStatus,
    string? ETag,
    IReadOnlyList<DeliveryItemSnapshot> Items);

public sealed record DeliveryItemSnapshot(
    string DeliveryDocumentItem,
    string? Material,
    decimal? ActualDeliveryQuantity,
    string? DeliveryQuantityUnit,
    string? ReferenceSDDocument,
    string? ReferenceSDDocumentItem);

// ---------- MM ----------

public sealed record PurchaseOrderSnapshot(
    string PurchaseOrder,
    string? PurchaseOrderType,
    string? Supplier,
    string? CompanyCode,
    IReadOnlyList<PurchaseOrderItemSnapshot> Items,
    bool? PossuiContratoAcm = null);

public sealed record PurchaseOrderItemSnapshot(
    string PurchaseOrderItem,
    string? Material,
    decimal? OrderQuantity,
    string? PurchaseOrderQuantityUnit,
    string? Plant);

// Resultado do lancamento de documento de material (ex.: movimento 101).
public sealed record MaterialMovementResult(
    string MaterialDocument,
    string MaterialDocumentYear);

// ---------- ACM ----------

// Comando de pesagem JA MAPEADO entregue ao adapter de transporte. O adapter NAO deriva
// TipoOperacaoSap nem transforma NumeroContrato -> ContratoACM: recebe tudo pronto.
public sealed record SapWeighingCommand(
    string ResourcePath,
    IReadOnlyDictionary<string, object?> Payload);
