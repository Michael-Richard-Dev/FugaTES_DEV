using FugaTES.Aplicacao.Contratos.Sd;
using FugaTES.Aplicacao.Modelos.Sd;
using Npgsql;

namespace FugaTES.Persistencia.Sd;

/// <summary>
/// Devolutivas do Guardian em fugates.devolutiva_sd_pesagem (migracao 007S2).
///
/// A listagem junta o protocolo da linha correlacionada, quando existe, porque e isso que fecha
/// o circuito na tela: o protocolo que mandamos e a pesagem que voltou, lado a lado.
/// </summary>
public sealed class DevolutivasSdPostgreSql(NpgsqlDataSource fonte) : IDevolutivasSdRepositorio
{
    public async Task<IReadOnlyList<DevolutivaSdLinha>> ListarAsync(int limite, CancellationToken ct)
    {
        await using var comando = fonte.CreateCommand("""
            select d.id_pesagem, d.centro, d.data_pesagem, d.peso_bruto, d.peso_tara, d.peso_liquido,
                   d.chave_natural, d.ordem_venda, d.linha_ordem, d.placa, d.nome_motorista,
                   d.estado, d.recebido_em, d.payload_json, p.protocolo_id
              from fugates.devolutiva_sd_pesagem d
              left join fugates.protocolo_sd_ordem_venda p on p.chave_natural = d.chave_natural
             order by d.recebido_em desc
             limit $1
            """);
        comando.Parameters.AddWithValue(limite is > 0 and <= 1000 ? limite : 200);
        await using var leitor = await comando.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var itens = new List<DevolutivaSdLinha>();
        while (await leitor.ReadAsync(ct).ConfigureAwait(false))
            itens.Add(new DevolutivaSdLinha(
                IdPesagem: leitor.GetString(0),
                Centro: leitor.GetString(1),
                DataPesagem: leitor.GetDateTime(2),
                PesoBruto: leitor.GetDecimal(3),
                PesoTara: leitor.GetDecimal(4),
                PesoLiquido: leitor.GetDecimal(5),
                ChaveNatural: leitor.IsDBNull(6) ? null : leitor.GetString(6),
                OrdemVenda: leitor.IsDBNull(7) ? null : leitor.GetString(7),
                LinhaOrdem: leitor.IsDBNull(8) ? null : leitor.GetString(8),
                Placa: leitor.IsDBNull(9) ? null : leitor.GetString(9),
                NomeMotorista: leitor.IsDBNull(10) ? null : leitor.GetString(10),
                Estado: leitor.GetString(11),
                RecebidoEm: leitor.GetFieldValue<DateTimeOffset>(12),
                PayloadJson: leitor.GetString(13))
            { ProtocoloId = leitor.IsDBNull(14) ? null : leitor.GetInt64(14) });
        return itens;
    }

    public async Task RegistrarAsync(DevolutivaPesagemSd devolutiva, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devolutiva);
        await using var comando = fonte.CreateCommand("""
            insert into fugates.devolutiva_sd_pesagem
                (id, id_pesagem, centro, tipo_pesagem, data_pesagem, peso_bruto, peso_tara,
                 peso_liquido, chave_natural, ordem_venda, linha_ordem, placa, nome_motorista,
                 estado, payload_json, recebido_em, atualizado_em)
            values (gen_random_uuid(), $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, now(), now())
            """);
        comando.Parameters.AddWithValue(devolutiva.IdPesagem);
        comando.Parameters.AddWithValue(devolutiva.Centro);
        comando.Parameters.AddWithValue(devolutiva.TipoPesagem);
        comando.Parameters.AddWithValue(devolutiva.DataPesagem);
        comando.Parameters.AddWithValue(devolutiva.PesoBruto);
        comando.Parameters.AddWithValue(devolutiva.PesoTara);
        comando.Parameters.AddWithValue(devolutiva.PesoLiquido);
        comando.Parameters.AddWithValue((object?)devolutiva.ChaveNatural ?? DBNull.Value);
        comando.Parameters.AddWithValue((object?)devolutiva.OrdemVenda ?? DBNull.Value);
        comando.Parameters.AddWithValue((object?)devolutiva.LinhaOrdem ?? DBNull.Value);
        comando.Parameters.AddWithValue((object?)devolutiva.Placa ?? DBNull.Value);
        comando.Parameters.AddWithValue((object?)devolutiva.NomeMotorista ?? DBNull.Value);
        comando.Parameters.AddWithValue(devolutiva.TemCorrelacao
            ? EstadoDevolutivaSd.Correlacionada : EstadoDevolutivaSd.SemCorrelacao);
        comando.Parameters.AddWithValue(devolutiva.PayloadJson);

        try { await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
        // 23505 = unique_violation: o Guardian reenviou a mesma pesagem.
        catch (PostgresException erro) when (erro.SqlState == "23505")
        { throw new DevolutivaSdJaRecebidaException(devolutiva.IdPesagem); }
    }
}
