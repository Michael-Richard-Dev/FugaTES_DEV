using FugaTES.Aplicacao.Configuracao;
using System.Data;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.Monitor;
using Npgsql;
using NpgsqlTypes;

namespace FugaTES.Persistencia.Consultas.Monitor;

// roleEsperada nula mantem o comportamento historico: identidade fixa na role ACM compartilhada.
public sealed partial class MonitorPostgreSqlReadRepository(NpgsqlDataSource source, bool exigirIdentidadeAcm, string? roleEsperada,AmbienteBanco? ambiente) : IMonitorReadRepository
{
    public MonitorPostgreSqlReadRepository(NpgsqlDataSource source,bool exigirIdentidadeAcm=false,string? roleEsperada=null):this(source,exigirIdentidadeAcm,roleEsperada,null){}
    // Every read is in a short, database-enforced read-only transaction.
    private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> AbrirAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        var connection = await source.OpenConnectionAsync(ct);
        try
        {
            var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            try
            {
                await using var command = new NpgsqlCommand("SET TRANSACTION READ ONLY;", connection, tx);
                await command.ExecuteNonQueryAsync(ct);
                if (exigirIdentidadeAcm)
                {
                    await using var identity = ComandoIdentidade(roleEsperada, connection, tx,ambiente);
                    if (await identity.ExecuteScalarAsync(ct) is not true)
                        throw new InvalidOperationException("ACM_DATABASE_IDENTITY_BLOCKED");
                }
                return (connection, tx);
            }
            catch { await tx.DisposeAsync(); throw; }
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task<bool> ExisteAsync(string tabela, NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select to_regclass(@table) is not null;", connection, tx);
        cmd.Parameters.AddWithValue("table", tabela);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filtro, CancellationToken ct)
    {
        var handles = await AbrirAsync(source, ct);
        await using var connection = handles.Connection;
        await using var tx = handles.Transaction;
        var projection = await ExisteAsync("fugates.projecao_resultado_fluxo_mvp", connection, tx, ct);
        var events = await ExisteAsync("fugates.correlacao_guardian_evento", connection, tx, ct);
        var correlation = await ExisteAsync("fugates.correlacao_guardian_sap", connection, tx, ct);
        var sql = ConstruirSql(projection, events, correlation);
        await using var cmd = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 10 };
        Texto(cmd, "op", filtro.Operacao); Texto(cmd, "state", filtro.Estado);
        Texto(cmd, "corr", filtro.CorrelationId); Texto(cmd, "doc", filtro.Documento);
        Texto(cmd, "group", filtro.Grupo);
        Texto(cmd, "document_operation", MonitorOperacionalConsulta.OperacaoCorrelacionadaPorDocumento);
        cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, (object?)filtro.De?.ToUniversalTime() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, (object?)filtro.Ate?.ToUniversalTime() ?? DBNull.Value);
        var rows = await LerAsync<MonitorIntegracao>(cmd, ct);
        await tx.CommitAsync(ct);
        return new(projection, events, rows);
    }

    public static string ConstruirSql(bool projection, bool events, bool correlation)
    {
        var p = projection ? "left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id" : "";
        var c = correlation ? "left join fugates.correlacao_guardian_sap c on " +
            (events ? "c.id=(select e.correlacao_id from fugates.correlacao_guardian_evento e where e.integracao_id=i.id)" : "false") +
            " or (i.operacao=@document_operation and c.documento_logistico_referencia=i.chave_negocio)" +
            (projection ? " or c.protocolo_guardian=p.protocolo_guardian" : "") : "";
        var projected = projection ? """
            p.fluxo as "Fluxo", p.status_funcional as "StatusFuncional", p.etapa_funcional as "EtapaFuncional",
            p.decisao_funcional_pendente as "DecisaoPendente", p.sales_order as "SalesOrder",
            p.delivery_document as "DeliveryDocument", p.purchase_order as "PurchaseOrder",
            p.material_document as "MaterialDocument", p.material_document_year as "MaterialDocumentYear",
            p.numero_ticket_guardian as "NumeroTicketGuardian",
            coalesce(p.resultado_funcional,t.desfecho) as "UltimoResultado", p.erro_codigo as "UltimoErro",
            """ : "t.desfecho as \"UltimoResultado\", null::text as \"UltimoErro\",";
        return $$"""
            select row_to_json(m)::text from (
              select i.id as "IntegracaoId", i.criado_em as "CriadoEm", i.operacao as "Operacao",
                i.correlation_id as "CorrelationId", i.estado as "EstadoMotor", i.tentativa_atual_id as "Tentativa",
                i.chave_negocio as "Documento", i.atualizado_em as "AtualizadoEm",
                {{(correlation ? "c.protocolo_guardian" : "null::text")}} as "ProtocoloGuardian",
                {{(correlation ? "c.ticket_guardian" : "null::text")}} as "TicketGuardian", {{projected}}
                {{(correlation ? "c.estado" : "null::text")}} as "EstadoCorrelacao",
                i.id as "RowKey"
              from fugates.integracao i
              left join fugates.tentativa_integracao t on t.id=i.tentativa_atual_id {{p}} {{c}}
              where (@op is null or i.operacao=@op {{(projection ? "or p.fluxo=@op" : "")}})
                and (@state is null or i.estado=@state)
                and (@corr is null or strpos(i.correlation_id,@corr)>0)
                and (@doc is null or strpos(i.chave_negocio,@doc)>0 {{(projection ? "or @doc in (p.sales_order,p.delivery_document,p.purchase_order,p.material_document)" : "")}})
                and (@from is null or i.criado_em>=@from) and (@to is null or i.criado_em<=@to)
                and (@group='TODOS'
                  or (@group='PENDENTES' and i.estado in ('PENDENTE','EM_PROCESSAMENTO','AGUARDANDO_REPROCESSAMENTO','AGUARDANDO_ACAO_MANUAL','RESULTADO_INDETERMINADO') {{(projection ? "or @group='PENDENTES' and p.status_funcional='AGUARDANDO_CALLBACK'" : "")}})
                  or (@group='FALHAS' and i.estado in ('AGUARDANDO_REPROCESSAMENTO','FALHA_DEFINITIVA'))
                  or (@group='INDETERMINADOS' and i.estado='RESULTADO_INDETERMINADO'))
              order by i.criado_em desc, i.id limit 500
            ) m;
            """;
    }

    public async Task<MonitorDetalhe> DetalharAsync(Guid id, CancellationToken ct)
        => (await DetalharCoreAsync(id, ct, incluirAcm: false)).Detalhe;

    public Task<MonitorAcmDetalhe> DetalharAcmAsync(Guid id, CancellationToken ct)
        => DetalharCoreAsync(id, ct, incluirAcm: true);

    private async Task<MonitorAcmDetalhe> DetalharCoreAsync(Guid id, CancellationToken ct, bool incluirAcm)
    {
        var handles = await AbrirAsync(source, ct);
        await using var connection = handles.Connection; await using var tx = handles.Transaction;
        async Task<IReadOnlyList<T>> Read<T>(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 10 };
            cmd.Parameters.AddWithValue("id", id);
            if (sql.Contains("@document_operation")) Texto(cmd, "document_operation", MonitorOperacionalConsulta.OperacaoCorrelacionadaPorDocumento);
            return await LerAsync<T>(cmd, ct);
        }
        var attempts = await Read<MonitorTentativa>("""
            select row_to_json(m)::text from (select id as "Id", fencing_token as "FencingToken",
              tipo_disparo as "TipoDisparo", fase_envio as "FaseEnvio", desfecho as "Desfecho",
              criada_em as "CriadaEm", envio_iniciado_em as "EnvioIniciadoEm", resultado_recebido_em as "ResultadoRecebidoEm"
              from fugates.tentativa_integracao where integracao_id=@id order by criada_em,id) m;
            """);
        var audit = await Read<MonitorAuditoria>("""
            select row_to_json(m)::text from (select evento as "Evento", criada_em as "CriadaEm",
              fencing_token as "FencingToken", tentativa_id as "TentativaId"
              from fugates.auditoria_integracao where integracao_id=@id order by criada_em,id) m;
            """);
        IReadOnlyList<MonitorEventoGuardian> guardian = [];
        if (await ExisteAsync("fugates.correlacao_guardian_evento", connection, tx, ct))
        {
            var correlation = await ExisteAsync("fugates.correlacao_guardian_sap", connection, tx, ct);
            var projection = await ExisteAsync("fugates.projecao_resultado_fluxo_mvp", connection, tx, ct);
            var links = correlation ? "union select c.id from fugates.correlacao_guardian_sap c join fugates.integracao i on i.id=@id and i.operacao=@document_operation and c.documento_logistico_referencia=i.chave_negocio" : "";
            if (correlation && projection) links += " union select c.id from fugates.correlacao_guardian_sap c join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=@id and p.protocolo_guardian=c.protocolo_guardian";
            guardian = await Read<MonitorEventoGuardian>($$"""
                select row_to_json(m)::text from (select estado_codigo as "EstadoCodigo", sequencial_guardian as "Sequencial",
                  peso_bruto as "PesoBruto", tara_final as "Tara", peso_liquido as "PesoLiquido", recebido_em as "RecebidoEm"
                  from fugates.correlacao_guardian_evento where correlacao_id in
                    (select correlacao_id from fugates.correlacao_guardian_evento where integracao_id=@id {{links}})
                  order by recebido_em,id) m;
                """);
        }
        var acm = incluirAcm ? await LerAcmAsync(id, connection, tx, ct) : new MonitorAcmDados();
        await tx.CommitAsync(ct);
        return new(new(attempts, audit.Select(a => a with { Evento = MonitorOperacionalConsulta.TextoSeguro(a.Evento)! }).ToArray(), guardian), acm.Sanitizar());
    }

    private static void Texto(NpgsqlCommand cmd, string nome, string? value)
        => cmd.Parameters.AddWithValue(nome, NpgsqlDbType.Text, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim());
    private static async Task<IReadOnlyList<T>> LerAsync<T>(NpgsqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<T>(); await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
        return rows;
    }
}
