using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Http;

namespace FugaTES.Integracao.SAP.BusinessDocuments;

internal static class SapEscrita
{
    public static StatusEscritaSap MapearFalha(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => StatusEscritaSap.NaoEncontrado,
        HttpStatusCode.Conflict => StatusEscritaSap.Conflito,
        HttpStatusCode.PreconditionFailed => StatusEscritaSap.PreCondicaoFalhou,
        HttpStatusCode.Unauthorized => StatusEscritaSap.FalhaAutenticacao,
        HttpStatusCode.Forbidden => StatusEscritaSap.FalhaAutorizacao,
        HttpStatusCode.BadRequest => StatusEscritaSap.FalhaContrato,
        >= HttpStatusCode.InternalServerError => StatusEscritaSap.FalhaTecnica,
        _ => StatusEscritaSap.FalhaTecnica
    };

    public static bool Sucesso(HttpStatusCode s)
        => s is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent;
}

// SD: Outbound Delivery GET-por-referencia / CREATE / PATCH (CSRF, ETag, If-Match).
public sealed class RemessaSapAdapter(SapODataHttpExecutor executor) : IRemessaSap
{
    private const string Servico = "/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2";
    private const string HeaderEntity = "A_OutbDeliveryHeader";
    private const string ItemEntity = "A_OutbDeliveryItem";

    public async Task<ResultadoConsultaSap<DeliverySnapshot>> ObterPorDocumentoAsync(
        string deliveryDocumentCanonico, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deliveryDocumentCanonico) || deliveryDocumentCanonico != deliveryDocumentCanonico.Trim())
            return ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsultaSap.FalhaContrato);
        try
        {
            var uri = executor.ConstruirUri(SapKeyPath.Single(Servico, HeaderEntity, deliveryDocumentCanonico), "$format=json");
            var resposta = await executor.ObterAsync(uri, cancellationToken).ConfigureAwait(false);
            if (resposta.Status != HttpStatusCode.OK)
                return ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsulta(resposta.Status));
            using var doc = JsonDocument.Parse(resposta.Body);
            var snapshot = MapearDelivery(SapConsultaAdapterBase.RaizEntidade(doc.RootElement), resposta.ETag);
            if (snapshot is null || !MesmoDocumentoConsultado(deliveryDocumentCanonico, snapshot.DeliveryDocument))
                return ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsultaSap.FalhaContrato);
            return ResultadoConsultaSap<DeliverySnapshot>.Sucesso(snapshot with { DeliveryDocument = deliveryDocumentCanonico });
        }
        catch (SapODataHttpExecutor.ConfiguracaoInvalidaException)
        { return ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsultaSap.FalhaConfiguracao); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsultaSap.FalhaTecnica); }
        catch (HttpRequestException) { return ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsultaSap.FalhaTecnica); }
        catch (JsonException) { return ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsultaSap.FalhaContrato); }
    }

    private static bool MesmoDocumentoConsultado(string canonical, string wire)
        => canonical == wire ||
            (canonical.All(c => c is >= '0' and <= '9') && wire.All(c => c is >= '0' and <= '9') &&
             canonical.TrimStart('0') == wire.TrimStart('0'));

    public async Task<ResultadoConsultaSap<DeliverySnapshot>> ObterPorOrdemVendaAsync(string salesOrder, CancellationToken cancellationToken)
    {
        var candidatas = await ObterCandidatasPorOrdemVendaAsync(salesOrder, cancellationToken).ConfigureAwait(false);
        if (candidatas.Status != StatusConsultaSap.Sucesso)
            return ResultadoConsultaSap<DeliverySnapshot>.Falha(candidatas.Status);
        return candidatas.Dados!.Count == 1
            ? ResultadoConsultaSap<DeliverySnapshot>.Sucesso(candidatas.Dados[0])
            : ResultadoConsultaSap<DeliverySnapshot>.Falha(StatusConsultaSap.FalhaContrato);
    }

    public async Task<ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>> ObterCandidatasPorOrdemVendaAsync(
        string salesOrder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(salesOrder))
            return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsultaSap.FalhaContrato);
        try
        {
            var filtro = "ReferenceSDDocument eq '" + salesOrder.Replace("'", "''", StringComparison.Ordinal) + "'";
            var resposta = await executor.ObterAsync(executor.ConstruirUri(Servico + "/" + ItemEntity,
                "$format=json&$filter=" + Uri.EscapeDataString(filtro)), cancellationToken).ConfigureAwait(false);
            if (resposta.Status != HttpStatusCode.OK)
                return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsulta(resposta.Status));
            using var itensDoc = JsonDocument.Parse(resposta.Body);
            var itens = ColecaoV2(itensDoc.RootElement);
            if (itens.GetArrayLength() == 0)
                return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsultaSap.NaoEncontrado);
            var grupos = new Dictionary<string, List<DeliveryItemSnapshot>>(StringComparer.Ordinal);
            foreach (var item in itens.EnumerateArray())
            {
                var id = SapConsultaAdapterBase.Texto(item, "DeliveryDocument");
                if (string.IsNullOrWhiteSpace(id) || SapConsultaAdapterBase.Texto(item, "ReferenceSDDocument") != salesOrder)
                    throw new JsonException("Delivery item identity is incompatible.");
                if (!grupos.TryGetValue(id, out var lista)) grupos[id] = lista = [];
                lista.Add(new(SapConsultaAdapterBase.Texto(item, "DeliveryDocumentItem") ?? string.Empty,
                    SapConsultaAdapterBase.Texto(item, "Material"), SapConsultaAdapterBase.Decimal(item, "ActualDeliveryQuantity"),
                    SapConsultaAdapterBase.Texto(item, "DeliveryQuantityUnit"), salesOrder,
                    SapConsultaAdapterBase.Texto(item, "ReferenceSDDocumentItem")));
            }
            var snapshots = new List<DeliverySnapshot>();
            foreach (var grupo in grupos)
            {
                var headerFilter = "DeliveryDocument eq '" + grupo.Key.Replace("'", "''", StringComparison.Ordinal) + "'";
                var header = await executor.ObterAsync(executor.ConstruirUri(Servico + "/" + HeaderEntity,
                    "$format=json&$filter=" + Uri.EscapeDataString(headerFilter)), cancellationToken).ConfigureAwait(false);
                if (header.Status != HttpStatusCode.OK)
                    return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsulta(header.Status));
                using var headerDoc = JsonDocument.Parse(header.Body);
                var entidade = PrimeiraDaColecao(headerDoc.RootElement);
                var snapshot = entidade is null ? null : MapearDelivery(entidade.Value, header.ETag);
                if (snapshot is null || snapshot.DeliveryDocument != grupo.Key)
                    throw new JsonException("Delivery header identity is incompatible.");
                snapshots.Add(snapshot with { Items = grupo.Value.ToArray() });
            }
            return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Sucesso(snapshots);
        }
        catch (SapODataHttpExecutor.ConfiguracaoInvalidaException)
        {
            return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsultaSap.FalhaConfiguracao);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsultaSap.FalhaTecnica); }
        catch (HttpRequestException) { return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsultaSap.FalhaTecnica); }
        catch (JsonException) { return ResultadoConsultaSap<IReadOnlyList<DeliverySnapshot>>.Falha(StatusConsultaSap.FalhaContrato); }
    }

    private static StatusConsultaSap StatusConsulta(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => StatusConsultaSap.NaoEncontrado,
        HttpStatusCode.Unauthorized => StatusConsultaSap.FalhaAutenticacao,
        HttpStatusCode.Forbidden => StatusConsultaSap.FalhaAutorizacao,
        HttpStatusCode.BadRequest => StatusConsultaSap.FalhaContrato,
        _ => StatusConsultaSap.FalhaTecnica
    };

    public Task<ResultadoEscritaSap<DeliverySnapshot>> CriarAsync(CriarRemessaComando comando, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comando);
        cancellationToken.ThrowIfCancellationRequested();
        // Exact DS4 write resource has not been approved. Do not fetch CSRF or send HTTP.
        return Task.FromResult(ResultadoEscritaSap<DeliverySnapshot>.Falha(StatusEscritaSap.FalhaConfiguracao));
    }

    public Task<ResultadoEscritaSap<DeliverySnapshot>> AtualizarAsync(string deliveryDocument, string etag, IReadOnlyDictionary<string, object?> patchCampos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patchCampos);
        if (string.IsNullOrWhiteSpace(etag))
        {
            // If-Match obrigatorio para PATCH condicional: sem ETag e fail-closed.
            return Task.FromResult(ResultadoEscritaSap<DeliverySnapshot>.Falha(StatusEscritaSap.PreCondicaoFalhou));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ResultadoEscritaSap<DeliverySnapshot>.Falha(StatusEscritaSap.FalhaConfiguracao));
    }

    private async Task<ResultadoEscritaSap<DeliverySnapshot>> EscreverAsync(
        HttpMethod metodo, string path, string? ifMatch, Func<HttpContent> conteudo, CancellationToken cancellationToken, string? deliveryDocumentFallback = null)
    {
        Uri uri;
        try { uri = executor.ConstruirUri(path); }
        catch (SapODataHttpExecutor.ConfiguracaoInvalidaException)
        {
            return ResultadoEscritaSap<DeliverySnapshot>.Falha(StatusEscritaSap.FalhaConfiguracao);
        }

        try
        {
            var token = await executor.ObterTokenCsrfAsync(uri, cancellationToken).ConfigureAwait(false);
            var r = await executor.EnviarComCsrfAsync(metodo, uri, conteudo(), token, ifMatch, cancellationToken).ConfigureAwait(false);
            if (!SapEscrita.Sucesso(r.Status))
            {
                return ResultadoEscritaSap<DeliverySnapshot>.Falha(SapEscrita.MapearFalha(r.Status));
            }

            DeliverySnapshot? snap = null;
            if (!string.IsNullOrWhiteSpace(r.Body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(r.Body);
                    snap = MapearDelivery(SapConsultaAdapterBase.RaizEntidade(doc.RootElement), r.ETag);
                }
                catch (JsonException)
                {
                    return ResultadoEscritaSap<DeliverySnapshot>.Falha(StatusEscritaSap.FalhaContrato);
                }
            }

            snap ??= deliveryDocumentFallback is not null
                ? new DeliverySnapshot(deliveryDocumentFallback, null, null, null, r.ETag, [])
                : null;

            return snap is null
                ? ResultadoEscritaSap<DeliverySnapshot>.Falha(StatusEscritaSap.FalhaContrato)
                : ResultadoEscritaSap<DeliverySnapshot>.Sucesso(snap);
        }
        catch (SapHttpStatusException ex) { return ResultadoEscritaSap<DeliverySnapshot>.Falha(SapEscrita.MapearFalha(ex.StatusCode)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ResultadoEscritaSap<DeliverySnapshot>.Falha(StatusEscritaSap.FalhaTecnica); }
        catch (HttpRequestException) { return ResultadoEscritaSap<DeliverySnapshot>.Falha(StatusEscritaSap.FalhaTecnica); }
    }

    private static JsonElement? PrimeiraDaColecao(JsonElement root)
    {
        var results = ColecaoV2(root);
        if (results.GetArrayLength() > 1) throw new JsonException("Delivery header is ambiguous.");
        return results.GetArrayLength() == 0 ? null : results[0];
    }

    private static JsonElement ColecaoV2(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("d", out var d) ||
            d.ValueKind != JsonValueKind.Object || d.TryGetProperty("__next", out _) ||
            !d.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a complete V2 Delivery collection.");
        return results;
    }

    private static DeliverySnapshot? MapearDelivery(JsonElement e, string? etagHeader)
    {
        var id = SapConsultaAdapterBase.Texto(e, "DeliveryDocument");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var itens = SapConsultaAdapterBase.Colecao(e, "to_DeliveryDocumentItem")
            .Select(i => new DeliveryItemSnapshot(
                SapConsultaAdapterBase.Texto(i, "DeliveryDocumentItem") ?? string.Empty,
                SapConsultaAdapterBase.Texto(i, "Material"),
                SapConsultaAdapterBase.Decimal(i, "ActualDeliveryQuantity"),
                SapConsultaAdapterBase.Texto(i, "DeliveryQuantityUnit"),
                SapConsultaAdapterBase.Texto(i, "ReferenceSDDocument"),
                SapConsultaAdapterBase.Texto(i, "ReferenceSDDocumentItem")))
            .ToArray();

        return new DeliverySnapshot(
            id,
            SapConsultaAdapterBase.Texto(e, "DeliveryDocumentType"),
            SapConsultaAdapterBase.Texto(e, "ShippingPoint"),
            SapConsultaAdapterBase.Texto(e, "OverallGoodsMovementStatus"),
            SapConsultaAdapterBase.Texto(e, "@odata.etag") ?? etagHeader ??
                (e.TryGetProperty("__metadata", out var metadata) ? SapConsultaAdapterBase.Texto(metadata, "etag") : null),
            itens);
    }
}

// MM: Material Document 101 POST (API_MATERIAL_DOCUMENT_SRV) com CSRF; captura MaterialDocument + Year.
public sealed class MovimentoMaterialSapAdapter(SapODataHttpExecutor executor) : IMovimentoMaterialSap
{
    private const string Path = "/sap/opu/odata/sap/API_MATERIAL_DOCUMENT_SRV/A_MaterialDocumentHeader";

    public async Task<ResultadoEscritaSap<MaterialMovementResult>> LancarAsync(MovimentoMaterialComando comando, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comando);
        Uri uri;
        try { uri = executor.ConstruirUri(Path); }
        catch (SapODataHttpExecutor.ConfiguracaoInvalidaException)
        {
            return ResultadoEscritaSap<MaterialMovementResult>.Falha(StatusEscritaSap.FalhaConfiguracao);
        }

        try
        {
            var token = await executor.ObterTokenCsrfAsync(uri, cancellationToken).ConfigureAwait(false);
            var r = await executor.EnviarComCsrfAsync(HttpMethod.Post, uri, JsonContent.Create(comando.Payload), token, ifMatch: null, cancellationToken).ConfigureAwait(false);
            if (!SapEscrita.Sucesso(r.Status))
            {
                return ResultadoEscritaSap<MaterialMovementResult>.Falha(SapEscrita.MapearFalha(r.Status));
            }

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.Body) ? "{}" : r.Body);
                var e = SapConsultaAdapterBase.RaizEntidade(doc.RootElement);
                var matDoc = SapConsultaAdapterBase.Texto(e, "MaterialDocument");
                var matYear = SapConsultaAdapterBase.Texto(e, "MaterialDocumentYear");
                // Sem confirmacao do documento, o resultado nao e conclusivo: fail-closed (FalhaContrato).
                return string.IsNullOrWhiteSpace(matDoc) || string.IsNullOrWhiteSpace(matYear)
                    ? ResultadoEscritaSap<MaterialMovementResult>.Falha(StatusEscritaSap.FalhaContrato)
                    : ResultadoEscritaSap<MaterialMovementResult>.Sucesso(new MaterialMovementResult(matDoc!, matYear!));
            }
            catch (JsonException)
            {
                return ResultadoEscritaSap<MaterialMovementResult>.Falha(StatusEscritaSap.FalhaContrato);
            }
        }
        catch (SapHttpStatusException ex) { return ResultadoEscritaSap<MaterialMovementResult>.Falha(SapEscrita.MapearFalha(ex.StatusCode)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ResultadoEscritaSap<MaterialMovementResult>.Falha(StatusEscritaSap.FalhaTecnica); }
        catch (HttpRequestException) { return ResultadoEscritaSap<MaterialMovementResult>.Falha(StatusEscritaSap.FalhaTecnica); }
    }
}

// ACM: transporte ReceberPesagem dirigido por comando JA MAPEADO (sem semantica funcional).
public sealed class ReceberPesagemSapAdapter(SapODataHttpExecutor executor) : IReceberPesagemSap
{
    public async Task<ResultadoEscritaSap<string?>> EnviarAsync(SapWeighingCommand comando, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comando);
        if (string.IsNullOrWhiteSpace(comando.ResourcePath))
        {
            return ResultadoEscritaSap<string?>.Falha(StatusEscritaSap.FalhaConfiguracao);
        }

        Uri uri;
        try { uri = executor.ConstruirUri(comando.ResourcePath); }
        catch (SapODataHttpExecutor.ConfiguracaoInvalidaException)
        {
            return ResultadoEscritaSap<string?>.Falha(StatusEscritaSap.FalhaConfiguracao);
        }

        try
        {
            var token = await executor.ObterTokenCsrfAsync(uri, cancellationToken).ConfigureAwait(false);
            var r = await executor.EnviarComCsrfAsync(HttpMethod.Post, uri, JsonContent.Create(comando.Payload), token, ifMatch: null, cancellationToken).ConfigureAwait(false);
            return SapEscrita.Sucesso(r.Status)
                ? ResultadoEscritaSap<string?>.Sucesso(r.Body)
                : ResultadoEscritaSap<string?>.Falha(SapEscrita.MapearFalha(r.Status));
        }
        catch (SapHttpStatusException ex) { return ResultadoEscritaSap<string?>.Falha(SapEscrita.MapearFalha(ex.StatusCode)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ResultadoEscritaSap<string?>.Falha(StatusEscritaSap.FalhaTecnica); }
        catch (HttpRequestException) { return ResultadoEscritaSap<string?>.Falha(StatusEscritaSap.FalhaTecnica); }
    }
}
