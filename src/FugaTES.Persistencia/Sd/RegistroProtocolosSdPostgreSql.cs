using FugaTES.Aplicacao.Contratos.Sd;
using Npgsql;

namespace FugaTES.Persistencia.Sd;

/// <summary>
/// Registro de protocolos SD em fugates.protocolo_sd_ordem_venda (migracao 007S1).
///
/// Substitui o arquivo que ficava ao lado do executavel. O arquivo dava um registro por
/// instalacao: a instalacao que nao enviou nao sabia do protocolo criado pela outra e reenviaria
/// a mesma linha.
///
/// Quem decide a duplicidade e o indice unico do banco, nao uma conferencia previa. Entre ler
/// "nao existe" e gravar, outra janela pode ter gravado; so a restricao resolve isso.
/// </summary>
public sealed class RegistroProtocolosSdPostgreSql(NpgsqlDataSource fonte) : IRegistroProtocolosSd
{
    private const string Colunas =
        "chave_natural, protocolo_id, estado, criado_em, http_status";

    public async Task<ProtocoloSdRegistrado?> ProcurarAsync(string chaveNatural, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chaveNatural);
        await using var comando = fonte.CreateCommand(
            $"select {Colunas} from fugates.protocolo_sd_ordem_venda where chave_natural = $1");
        comando.Parameters.AddWithValue(chaveNatural);
        await using var leitor = await comando.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await leitor.ReadAsync(ct).ConfigureAwait(false) ? Mapear(leitor) : null;
    }

    public async Task<IReadOnlyList<ProtocoloSdRegistrado>> LerAsync(CancellationToken ct)
    {
        await using var comando = fonte.CreateCommand(
            $"select {Colunas} from fugates.protocolo_sd_ordem_venda order by criado_em");
        await using var leitor = await comando.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var itens = new List<ProtocoloSdRegistrado>();
        while (await leitor.ReadAsync(ct).ConfigureAwait(false)) itens.Add(Mapear(leitor));
        return itens;
    }

    public async Task RegistrarAsync(ProtocoloSdEnvio envio, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(envio);
        await using var comando = fonte.CreateCommand("""
            insert into fugates.protocolo_sd_ordem_venda
                (id, chave_natural, ordem_venda, linha_ordem, centro, codigo_item_guardian,
                 codigo_emissor_guardian, protocolo_id, estado, http_status, payload_sha256,
                 url_retorno, mensagem_recusa, criado_em, atualizado_em)
            values (gen_random_uuid(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, now(), now())
            on conflict (chave_natural) do update set
                 centro = excluded.centro,
                 codigo_item_guardian = excluded.codigo_item_guardian,
                 codigo_emissor_guardian = excluded.codigo_emissor_guardian,
                 protocolo_id = excluded.protocolo_id,
                 estado = excluded.estado,
                 http_status = excluded.http_status,
                 payload_sha256 = excluded.payload_sha256,
                 url_retorno = excluded.url_retorno,
                 -- A mensagem da recusa anterior sai: ela descrevia a tentativa que passou.
                 mensagem_recusa = null,
                 atualizado_em = now()
             where fugates.protocolo_sd_ordem_venda.estado = 'RECUSADO'
            """);
        comando.Parameters.AddWithValue(envio.ChaveNatural);
        comando.Parameters.AddWithValue(envio.OrdemVenda);
        comando.Parameters.AddWithValue(envio.LinhaOrdem);
        comando.Parameters.AddWithValue(envio.Centro);
        comando.Parameters.AddWithValue(envio.CodigoItemGuardian);
        comando.Parameters.AddWithValue(envio.CodigoEmissorGuardian);
        comando.Parameters.AddWithValue(envio.ProtocoloId.HasValue ? envio.ProtocoloId.Value : DBNull.Value);
        comando.Parameters.AddWithValue(envio.Estado);
        comando.Parameters.AddWithValue((short)envio.HttpStatus);
        comando.Parameters.AddWithValue(envio.PayloadSha256);
        comando.Parameters.AddWithValue(envio.UrlRetorno);
        comando.Parameters.AddWithValue((object?)envio.MensagemRecusa ?? DBNull.Value);

        int afetadas;
        try { afetadas = await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        // 23505 = unique_violation, agora so por protocolo_id repetido; chave_natural cai no
        // "on conflict". A corrida termina aqui, no banco.
        catch (PostgresException erro) when (erro.SqlState == "23505")
        { throw new ProtocoloSdJaRegistradoException(envio.ChaveNatural); }
        // Zero linhas = a chave ja tem desfecho que NAO autoriza nova tentativa.
        if (afetadas != 1) throw new ProtocoloSdJaRegistradoException(envio.ChaveNatural);
    }

    public async Task AtualizarDesfechoAsync(string chaveNatural, long? protocoloId, string estado,
        int httpStatus, string? mensagemRecusa, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chaveNatural);
        await using var comando = fonte.CreateCommand("""
            update fugates.protocolo_sd_ordem_venda
               set protocolo_id = $2, estado = $3, http_status = $4,
                   mensagem_recusa = $5, atualizado_em = now()
             where chave_natural = $1
            """);
        comando.Parameters.AddWithValue(chaveNatural);
        comando.Parameters.AddWithValue(protocoloId.HasValue ? protocoloId.Value : DBNull.Value);
        comando.Parameters.AddWithValue(estado);
        comando.Parameters.AddWithValue((short)httpStatus);
        comando.Parameters.AddWithValue((object?)mensagemRecusa ?? DBNull.Value);
        var afetadas = await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        // Zero linhas significa que a reserva sumiu entre o envio e o fechamento. Falhar alto e
        // melhor do que seguir como se tivesse gravado.
        if (afetadas != 1)
            throw new InvalidOperationException($"SD_DESFECHO_NAO_GRAVADO: {chaveNatural} ({afetadas} linhas).");
    }

    private static ProtocoloSdRegistrado Mapear(NpgsqlDataReader leitor) => new(
        ChaveNatural: leitor.GetString(0),
        ProtocoloId: leitor.IsDBNull(1) ? null : leitor.GetInt64(1),
        Estado: leitor.GetString(2),
        QuandoUtc: leitor.GetFieldValue<DateTimeOffset>(3),
        HttpStatus: leitor.GetInt16(4));
}
