using FugaTES.Aplicacao.Contratos.Sd;
using Npgsql;

namespace FugaTES.Persistencia.Sd;

/// <summary>
/// Remessas do fluxo SD em fugates.remessa_sd (migracao 007S3).
///
/// Mesma forma do protocolo: RESERVA antes do envio e fecha o desfecho depois. Remessa criada no
/// SAP nao se desfaz por aqui, entao uma queda no meio do POST precisa deixar a pesagem ocupada,
/// e nao livre para uma segunda tentativa.
/// </summary>
public sealed class RemessasSdPostgreSql(NpgsqlDataSource fonte) : IRemessasSdRepositorio
{
    private const string Colunas =
        "id_pesagem, chave_natural, numero_remessa, estado, quantidade, unidade, " +
        "setor_atividade, http_status, mensagens_sap, criado_em, ajuste_estado, ajuste_mensagens, " +
        "texto_estado, texto_mensagens, picking_estado, saida_estado, saida_mensagens, " +
        "payload_json, ajuste_payload_json, texto_payload_json";

    public async Task<IReadOnlyList<RemessaSdLinha>> ListarAsync(int limite, CancellationToken ct)
    {
        await using var comando = fonte.CreateCommand(
            $"select {Colunas} from fugates.remessa_sd order by criado_em desc limit $1");
        comando.Parameters.AddWithValue(limite is > 0 and <= 1000 ? limite : 200);
        await using var leitor = await comando.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var itens = new List<RemessaSdLinha>();
        while (await leitor.ReadAsync(ct).ConfigureAwait(false)) itens.Add(Mapear(leitor));
        return itens;
    }

    public async Task<RemessaSdLinha?> ProcurarPorPesagemAsync(string idPesagem, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPesagem);
        await using var comando = fonte.CreateCommand(
            $"select {Colunas} from fugates.remessa_sd where id_pesagem = $1");
        comando.Parameters.AddWithValue(idPesagem);
        await using var leitor = await comando.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await leitor.ReadAsync(ct).ConfigureAwait(false) ? Mapear(leitor) : null;
    }

    public async Task ReservarAsync(RemessaSdRegistro r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        // A reserva vale tambem como NOVA TENTATIVA depois de uma recusa: o "where estado =
        // 'RECUSADA'" no conflito e o que autoriza, e ele roda dentro do proprio comando, sem
        // leitura previa que ficaria velha. Quando a linha existente nao for recusada, nenhuma
        // linha e afetada e a excecao de duplicidade acontece como antes.
        await using var comando = fonte.CreateCommand("""
            insert into fugates.remessa_sd
                (id, id_pesagem, chave_natural, ordem_venda, linha_ordem, setor_atividade,
                 quantidade, unidade, numero_remessa, estado, http_status, payload_sha256,
                 mensagens_sap, payload_json, criado_em, atualizado_em)
            values (gen_random_uuid(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, now(), now())
            on conflict (id_pesagem) do update set
                 chave_natural = excluded.chave_natural,
                 ordem_venda = excluded.ordem_venda,
                 linha_ordem = excluded.linha_ordem,
                 setor_atividade = excluded.setor_atividade,
                 quantidade = excluded.quantidade,
                 unidade = excluded.unidade,
                 numero_remessa = excluded.numero_remessa,
                 estado = excluded.estado,
                 http_status = excluded.http_status,
                 payload_sha256 = excluded.payload_sha256,
                 -- A mensagem da recusa anterior sai: ela descrevia a tentativa que passou.
                 mensagens_sap = null,
                 payload_json = excluded.payload_json,
                 atualizado_em = now()
             where fugates.remessa_sd.estado = 'RECUSADA'
            """);
        comando.Parameters.AddWithValue(r.IdPesagem);
        comando.Parameters.AddWithValue(r.ChaveNatural);
        comando.Parameters.AddWithValue(r.OrdemVenda);
        comando.Parameters.AddWithValue(r.LinhaOrdem);
        comando.Parameters.AddWithValue(r.SetorAtividade);
        comando.Parameters.AddWithValue(r.Quantidade);
        comando.Parameters.AddWithValue(r.Unidade);
        comando.Parameters.AddWithValue((object?)r.NumeroRemessa ?? DBNull.Value);
        comando.Parameters.AddWithValue(r.Estado);
        comando.Parameters.AddWithValue((short)r.HttpStatus);
        comando.Parameters.AddWithValue(r.PayloadSha256);
        comando.Parameters.AddWithValue((object?)r.MensagensSap ?? DBNull.Value);
        comando.Parameters.AddWithValue((object?)r.PayloadJson ?? DBNull.Value);

        int afetadas;
        try { afetadas = await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        // 23505 agora so aparece por numero_remessa repetido; id_pesagem cai no "on conflict".
        catch (PostgresException erro) when (erro.SqlState == "23505")
        { throw new RemessaSdJaCriadaException(r.IdPesagem); }
        // Zero linhas = a pesagem ja tem desfecho que NAO autoriza nova tentativa.
        if (afetadas != 1) throw new RemessaSdJaCriadaException(r.IdPesagem);
    }

    public async Task FecharDesfechoAsync(string idPesagem, string? numeroRemessa, string estado,
        int httpStatus, string? mensagensSap, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPesagem);
        await using var comando = fonte.CreateCommand("""
            update fugates.remessa_sd
               set numero_remessa = $2, estado = $3, http_status = $4,
                   mensagens_sap = $5, atualizado_em = now()
             where id_pesagem = $1
            """);
        comando.Parameters.AddWithValue(idPesagem);
        comando.Parameters.AddWithValue((object?)numeroRemessa ?? DBNull.Value);
        comando.Parameters.AddWithValue(estado);
        comando.Parameters.AddWithValue((short)httpStatus);
        comando.Parameters.AddWithValue((object?)mensagensSap ?? DBNull.Value);
        var afetadas = await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // Zero linhas significa que a reserva sumiu entre o envio e o fechamento. Falhar alto e
        // melhor do que seguir como se tivesse gravado.
        if (afetadas != 1)
            throw new InvalidOperationException($"SD_REMESSA_DESFECHO_NAO_GRAVADO: {idPesagem} ({afetadas}).");
    }

    /// <summary>
    /// Reserva o passo 4. A condicao no proprio UPDATE e o arbitro da corrida, e ela aceita
    /// tambem o estado RECUSADA: o SAP recusou e nao criou nada, entao nova tentativa e o
    /// caminho esperado. Em 02/11/2026 isso travou a remessa 80003859 depois de um 412 no item.
    /// </summary>
    public async Task ReservarAjusteAsync(string idPesagem, string payloadSha256, string? payloadJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPesagem);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadSha256);
        await using var comando = fonte.CreateCommand("""
            update fugates.remessa_sd
               set ajuste_estado = 'DESFECHO_DESCONHECIDO', ajuste_payload_sha256 = $2, ajuste_payload_json = $3,
                   ajuste_em = now(), atualizado_em = now()
             where id_pesagem = $1 and (ajuste_estado is null or ajuste_estado = 'RECUSADA')
            """);
        comando.Parameters.AddWithValue(idPesagem);
        comando.Parameters.AddWithValue(payloadSha256);
        comando.Parameters.AddWithValue((object?)payloadJson ?? DBNull.Value);
        if (await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1) return;

        // Zero linhas tem duas causas bem diferentes; dizer qual poupa uma investigacao.
        var linha = await ProcurarPorPesagemAsync(idPesagem, ct).ConfigureAwait(false);
        if (linha is null)
            throw new InvalidOperationException($"SD_AJUSTE_REMESSA_INEXISTENTE: {idPesagem}.");
        throw new AjusteRemessaSdJaFeitoException(idPesagem);
    }

    public async Task FecharAjusteAsync(string idPesagem, string estado, int httpStatus,
        string? mensagens, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPesagem);
        await using var comando = fonte.CreateCommand("""
            update fugates.remessa_sd
               set ajuste_estado = $2, ajuste_http_status = $3, ajuste_mensagens = $4,
                   atualizado_em = now()
             where id_pesagem = $1
            """);
        comando.Parameters.AddWithValue(idPesagem);
        comando.Parameters.AddWithValue(estado);
        comando.Parameters.AddWithValue((short)httpStatus);
        comando.Parameters.AddWithValue((object?)mensagens ?? DBNull.Value);
        var afetadas = await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (afetadas != 1)
            throw new InvalidOperationException($"SD_AJUSTE_DESFECHO_NAO_GRAVADO: {idPesagem} ({afetadas}).");
    }

    /// <summary>Mesma forma do ajuste: a condicao no UPDATE e o arbitro, nao uma leitura previa.</summary>
    public async Task ReservarTextoAsync(string idPesagem, string textoSha256, string? textoJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPesagem);
        ArgumentException.ThrowIfNullOrWhiteSpace(textoSha256);
        await using var comando = fonte.CreateCommand("""
            update fugates.remessa_sd
               set texto_estado = 'DESFECHO_DESCONHECIDO', texto_sha256 = $2, texto_payload_json = $3,
                   texto_em = now(), atualizado_em = now()
             where id_pesagem = $1 and (texto_estado is null or texto_estado = 'RECUSADO')
            """);
        comando.Parameters.AddWithValue(idPesagem);
        comando.Parameters.AddWithValue(textoSha256);
        comando.Parameters.AddWithValue((object?)textoJson ?? DBNull.Value);
        if (await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1) return;

        var linha = await ProcurarPorPesagemAsync(idPesagem, ct).ConfigureAwait(false);
        if (linha is null)
            throw new InvalidOperationException($"SD_TEXTO_REMESSA_INEXISTENTE: {idPesagem}.");
        throw new TextoNfeSdJaGravadoException(idPesagem);
    }

    public async Task FecharTextoAsync(string idPesagem, string estado, int httpStatus, bool criou,
        string? mensagens, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPesagem);
        await using var comando = fonte.CreateCommand("""
            update fugates.remessa_sd
               set texto_estado = $2, texto_http_status = $3, texto_criou = $4,
                   texto_mensagens = $5, atualizado_em = now()
             where id_pesagem = $1
            """);
        comando.Parameters.AddWithValue(idPesagem);
        comando.Parameters.AddWithValue(estado);
        comando.Parameters.AddWithValue((short)httpStatus);
        comando.Parameters.AddWithValue(criou);
        comando.Parameters.AddWithValue((object?)mensagens ?? DBNull.Value);
        var afetadas = await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (afetadas != 1)
            throw new InvalidOperationException($"SD_TEXTO_DESFECHO_NAO_GRAVADO: {idPesagem} ({afetadas}).");
    }

    /// <summary>
    /// Passo 6. Nome de coluna nao entra em SQL vindo de parametro: a etapa escolhe entre DOIS
    /// comandos escritos por extenso, e qualquer outro valor e recusado.
    /// </summary>
    public async Task ReservarExpedicaoAsync(string idPesagem, string etapa, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPesagem);
        var sql = etapa switch
        {
            EtapaExpedicaoSd.Picking => """
                update fugates.remessa_sd
                   set picking_estado = 'DESFECHO_DESCONHECIDO', picking_em = now(),
                       atualizado_em = now()
                 where id_pesagem = $1 and (picking_estado is null or picking_estado = 'RECUSADO')
                """,
            EtapaExpedicaoSd.Saida => """
                update fugates.remessa_sd
                   set saida_estado = 'DESFECHO_DESCONHECIDO', saida_em = now(),
                       atualizado_em = now()
                 where id_pesagem = $1 and (saida_estado is null or saida_estado = 'RECUSADA')
                """,
            _ => throw new ArgumentException("SD_EXPEDICAO_ETAPA_DESCONHECIDA: " + etapa, nameof(etapa))
        };

        await using var comando = fonte.CreateCommand(sql);
        comando.Parameters.AddWithValue(idPesagem);
        if (await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1) return;

        var linha = await ProcurarPorPesagemAsync(idPesagem, ct).ConfigureAwait(false);
        if (linha is null)
            throw new InvalidOperationException($"SD_EXPEDICAO_REMESSA_INEXISTENTE: {idPesagem}.");
        throw new ExpedicaoSdJaFeitaException(idPesagem, etapa);
    }

    public async Task FecharExpedicaoAsync(string idPesagem, string etapa, string estado,
        int httpStatus, string? mensagens, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idPesagem);
        var sql = etapa switch
        {
            EtapaExpedicaoSd.Picking => """
                update fugates.remessa_sd
                   set picking_estado = $2, picking_http_status = $3, picking_mensagens = $4,
                       atualizado_em = now()
                 where id_pesagem = $1
                """,
            EtapaExpedicaoSd.Saida => """
                update fugates.remessa_sd
                   set saida_estado = $2, saida_http_status = $3, saida_mensagens = $4,
                       atualizado_em = now()
                 where id_pesagem = $1
                """,
            _ => throw new ArgumentException("SD_EXPEDICAO_ETAPA_DESCONHECIDA: " + etapa, nameof(etapa))
        };

        await using var comando = fonte.CreateCommand(sql);
        comando.Parameters.AddWithValue(idPesagem);
        comando.Parameters.AddWithValue(estado);
        comando.Parameters.AddWithValue((short)httpStatus);
        comando.Parameters.AddWithValue((object?)mensagens ?? DBNull.Value);
        var afetadas = await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (afetadas != 1)
            throw new InvalidOperationException(
                $"SD_EXPEDICAO_DESFECHO_NAO_GRAVADO: {idPesagem}/{etapa} ({afetadas}).");
    }

    private static RemessaSdLinha Mapear(NpgsqlDataReader l) => new(
        IdPesagem: l.GetString(0),
        ChaveNatural: l.GetString(1),
        NumeroRemessa: l.IsDBNull(2) ? null : l.GetString(2),
        Estado: l.GetString(3),
        Quantidade: l.GetString(4),
        Unidade: l.GetString(5),
        SetorAtividade: l.GetString(6),
        HttpStatus: l.GetInt16(7),
        MensagensSap: l.IsDBNull(8) ? null : l.GetString(8),
        CriadoEm: l.GetFieldValue<DateTimeOffset>(9))
    {
        AjusteEstado = l.IsDBNull(10) ? null : l.GetString(10),
        AjusteMensagens = l.IsDBNull(11) ? null : l.GetString(11),
        TextoEstado = l.IsDBNull(12) ? null : l.GetString(12),
        TextoMensagens = l.IsDBNull(13) ? null : l.GetString(13),
        PickingEstado = l.IsDBNull(14) ? null : l.GetString(14),
        SaidaEstado = l.IsDBNull(15) ? null : l.GetString(15),
        SaidaMensagens = l.IsDBNull(16) ? null : l.GetString(16),
        PayloadJson = l.IsDBNull(17) ? null : l.GetString(17),
        AjustePayloadJson = l.IsDBNull(18) ? null : l.GetString(18),
        TextoPayloadJson = l.IsDBNull(19) ? null : l.GetString(19)
    };
}
