using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;

namespace FugaTES.Aplicacao.Contratos.Gateways.SAP;

// ---------- SD OUT capabilities ----------

public interface IConsultaOrdemVendaSap
{
    Task<ResultadoConsultaSap<SalesOrderSnapshot>> ConsultarAsync(string salesOrder, CancellationToken cancellationToken);
}

public interface IConsultaPartnerSap
{
    Task<ResultadoConsultaSap<PartnerSnapshot>> ConsultarAsync(string businessPartner, CancellationToken cancellationToken);
}

public interface IConsultaMaterialSap
{
    Task<ResultadoConsultaSap<ProductSnapshot>> ConsultarAsync(string product, CancellationToken cancellationToken);
}

// Remessa (Outbound Delivery): GET por referencia (obrigatorio ANTES de CREATE), CREATE e PATCH.
// O adapter NAO decide QUANDO criar/atualizar; isso e uma EXPLICIT_PENDING_DECISION (ver IDecisaoRemessaSap).
public interface IRemessaSap
{
    // Explicit caller identity: preserve it only after querying that document in SAP.
    Task<ResultadoConsultaSap<DeliverySnapshot>> ObterPorDocumentoAsync(string deliveryDocumentCanonico, CancellationToken cancellationToken)
        => throw new FugaTES.Aplicacao.Servicos.SAP.DecisaoFuncionalPendenteSapException("SD_DELIVERY_CANONICAL_READ_REQUIRED");

    Task<ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>> ObterCandidatasPorOrdemVendaAsync(string salesOrder, CancellationToken cancellationToken)
        => throw new FugaTES.Aplicacao.Servicos.SAP.DecisaoFuncionalPendenteSapException("SD_DELIVERY_CANDIDATES_REQUIRED");

    // GET da remessa existente que referencia a SalesOrder informada (dedup antes de CREATE).
    Task<ResultadoConsultaSap<DeliverySnapshot>> ObterPorOrdemVendaAsync(string salesOrder, CancellationToken cancellationToken);

    Task<ResultadoEscritaSap<DeliverySnapshot>> CriarAsync(CriarRemessaComando comando, CancellationToken cancellationToken);

    // PATCH condicional por If-Match/ETag. patchCampos ja vem mapeado pelo chamador.
    Task<ResultadoEscritaSap<DeliverySnapshot>> AtualizarAsync(string deliveryDocument, string etag, IReadOnlyDictionary<string, object?> patchCampos, CancellationToken cancellationToken);
}

// Comando de criacao de remessa JA MAPEADO (o adapter nao infere regra funcional).
public sealed record CriarRemessaComando(string ReferenceSDDocument, IReadOnlyDictionary<string, object?> Payload);

// ---------- MM IN capabilities ----------

public interface IConsultaPedidoCompraSap
{
    Task<ResultadoConsultaSap<PurchaseOrderSnapshot>> ConsultarAsync(string purchaseOrder, CancellationToken cancellationToken);
}

// Movimento de material (ex.: 101). Recebe um documento JA MAPEADO; nao dispara automaticamente
// (o gatilho funcional e uma EXPLICIT_PENDING_DECISION, ver IGatilhoMovimentoMaterialSap).
public interface IMovimentoMaterialSap
{
    Task<ResultadoEscritaSap<MaterialMovementResult>> LancarAsync(MovimentoMaterialComando comando, CancellationToken cancellationToken);
}

public sealed record MovimentoMaterialComando(IReadOnlyDictionary<string, object?> Payload);

// ---------- ACM capability ----------

// Transporte ReceberPesagem: recebe um comando JA MAPEADO (ResourcePath + Payload) e executa o wire
// (CSRF fetch, cookie/session, POST, response, error). NAO deriva TipoOperacaoSap nem ContratoACM.
public interface IReceberPesagemSap
{
    Task<ResultadoEscritaSap<string?>> EnviarAsync(SapWeighingCommand comando, CancellationToken cancellationToken);
}
