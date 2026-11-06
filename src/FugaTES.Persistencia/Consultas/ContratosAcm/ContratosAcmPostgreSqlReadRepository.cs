using FugaTES.Aplicacao.Configuracao;
using System.Data;
using FugaTES.Aplicacao.Consultas.ContratosAcm;
using Npgsql;

namespace FugaTES.Persistencia.Consultas.ContratosAcm;

// roleEsperada nula mantem o comportamento historico: identidade fixa na role ACM compartilhada.
public sealed class ContratosAcmPostgreSqlReadRepository(NpgsqlDataSource source,bool exigirIdentidadeAcm=false,string? roleEsperada=null,AmbienteBanco? ambiente=null) : IContratosAcmReadRepository
{
    public const string ConsultaSql = """
        select v.payload_contrato->>'Centro', v.numero_contrato, v.versao, v.criado_em,
            v.payload_contrato->>'StatusContrato', i.estado, v.integracao_id,
            i.correlation_id, v.versao = identidade.ultima_versao,
            v.payload_contrato->>'Item', v.payload_contrato::text
        from fugates.contrato_sap_toledo_versao v
        join fugates.contrato_sap_toledo_identidade identidade on identidade.numero_contrato=v.numero_contrato
        join fugates.integracao i on i.id=v.integracao_id
        where @todos or v.payload_contrato->>'Centro' = @centro
        order by v.criado_em desc, v.numero_contrato, v.versao desc
        limit 500
        """;

    public async Task<IReadOnlyList<ContratoAcmLeitura>> ConsultarAsync(string centro, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(centro)) throw new ArgumentException("Informe o centro.", nameof(centro));
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx))
            await readOnly.ExecuteNonQueryAsync(ct);
        if(exigirIdentidadeAcm)
        {
            await using var identity=FugaTES.Persistencia.Consultas.Monitor.MonitorPostgreSqlReadRepository.ComandoIdentidade(roleEsperada,connection,tx,ambiente);
            if(await identity.ExecuteScalarAsync(ct) is not true)throw new InvalidOperationException("UNIFIED_DEV_DATABASE_IDENTITY_REJECTED");
        }
        await using var command = new NpgsqlCommand(ConsultaSql, connection, tx) { CommandTimeout = 10 };
        // Sentinela TODOS: dispensa a igualdade de centro sem remover o filtro parametrizado,
        // de modo que um contrato de outro centro nao fica invisivel. Ordem, limite e
        // transacao somente leitura permanecem exatamente os mesmos.
        var todos = ContratosAcmConsulta.EhTodosOsCentros(centro);
        command.Parameters.AddWithValue("todos", todos);
        command.Parameters.AddWithValue("centro", todos ? string.Empty : centro.Trim());
        var result = new List<ContratoAcmLeitura>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                result.Add(new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1),
                    reader.GetInt64(2), reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), reader.GetGuid(6), reader.GetString(7), reader.GetBoolean(8))
                {
                    // Item e payload vem da projecao persistida; ausentes ficam nulos,
                    // nunca preenchidos por suposicao.
                    Item = reader.IsDBNull(9) ? null : reader.GetString(9),
                    PayloadContratoJson = reader.IsDBNull(10) ? null : reader.GetString(10),
                });
        }
        await tx.CommitAsync(ct);
        return result;
    }
}
