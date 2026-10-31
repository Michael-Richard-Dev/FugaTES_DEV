using System.Data;
using System.Data.Common;
using FugaTES.Aplicacao.Contratos.Mvp;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Mvp;

namespace FugaTES.Persistencia.Mvp;

// Production supplies the existing NpgsqlDataSource; DbDataSource permits offline transaction doubles.
public sealed class RepositorioResultadoFluxoMvpPostgreSql(DbDataSource dataSource) : IResultadoFluxoMvpStore
{
    private const string ClaimPredicate = """
        i.id = @integracao_id and i.tentativa_atual_id = @tentativa_id
        and i.fencing_token = @fencing_token and i.worker_proprietario = @worker
        and i.estado = 'EM_PROCESSAMENTO' and i.lease_expira_em > clock_timestamp()
        and i.correlation_id = @correlation_id and i.operacao = @operacao
        """;

    private static readonly string LockClaimSql = $"select i.id from fugates.integracao i where {ClaimPredicate} for update;";
    private static readonly string UpsertSql = $$"""
        insert into fugates.projecao_resultado_fluxo_mvp
            (integracao_id, fluxo, status_funcional, etapa_funcional, resultado_funcional,
             decisao_funcional_pendente, sales_order, delivery_document, purchase_order,
             material_document, material_document_year, protocolo_guardian, numero_ticket_guardian,
             erro_codigo, erro_categoria, erro_etapa, erro_reprocessavel, erro_resultado_indeterminado,
             criado_em, atualizado_em)
        select i.id, @fluxo, @status_funcional, @etapa_funcional, @resultado_funcional,
             @decisao_funcional_pendente, @sales_order, @delivery_document, @purchase_order,
             @material_document, @material_document_year, @protocolo_guardian, @numero_ticket_guardian,
             @erro_codigo, @erro_categoria, @erro_etapa, @erro_reprocessavel, @erro_resultado_indeterminado,
             clock_timestamp(), clock_timestamp()
        from fugates.integracao i where {{ClaimPredicate}}
        on conflict (integracao_id) do update set
            fluxo = excluded.fluxo, status_funcional = excluded.status_funcional,
            etapa_funcional = excluded.etapa_funcional, resultado_funcional = excluded.resultado_funcional,
            decisao_funcional_pendente = excluded.decisao_funcional_pendente,
            sales_order = excluded.sales_order, delivery_document = excluded.delivery_document,
            purchase_order = excluded.purchase_order, material_document = excluded.material_document,
            material_document_year = excluded.material_document_year,
            protocolo_guardian = excluded.protocolo_guardian, numero_ticket_guardian = excluded.numero_ticket_guardian,
            erro_codigo = excluded.erro_codigo, erro_categoria = excluded.erro_categoria,
            erro_etapa = excluded.erro_etapa, erro_reprocessavel = excluded.erro_reprocessavel,
            erro_resultado_indeterminado = excluded.erro_resultado_indeterminado,
            atualizado_em = clock_timestamp()
        returning integracao_id;
        """;

    private static string StatusWire(StatusFluxoMvp status) => status switch
    {
        StatusFluxoMvp.Recebido => "RECEBIDO",
        StatusFluxoMvp.Validado => "VALIDADO",
        StatusFluxoMvp.AguardandoDecisaoFuncional => "AGUARDANDO_DECISAO_FUNCIONAL",
        StatusFluxoMvp.AguardandoDependenciaExterna => "AGUARDANDO_DEPENDENCIA_EXTERNA",
        StatusFluxoMvp.ProntoParaEnvio => "PRONTO_PARA_ENVIO",
        StatusFluxoMvp.EmProcessamento => "EM_PROCESSAMENTO",
        StatusFluxoMvp.AguardandoCallback => "AGUARDANDO_CALLBACK",
        StatusFluxoMvp.Concluido => "CONCLUIDO",
        StatusFluxoMvp.FalhaReprocessavel => "FALHA_REPROCESSAVEL",
        StatusFluxoMvp.ResultadoIndeterminado => "RESULTADO_INDETERMINADO",
        StatusFluxoMvp.FalhaNaoReprocessavel => "FALHA_NAO_REPROCESSAVEL",
        _ => throw new ArgumentException("MVP_STATUS_UNKNOWN")
    };

    public async Task ExigirDisponibilidadeAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select to_regclass('fugates.projecao_resultado_fluxo_mvp') is not null;");
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new CapabilityMvpPendenteException("MVP_RESULT_DURABLE_PROJECTION");
    }

    public Task<ResultadoFluxoMvp?> ObterAsync(ClaimIntegracaoMotor claim, CancellationToken cancellationToken)
        => ObterPorIntegracaoAsync(claim.IntegracaoId, claim.CorrelationId, claim.Operacao, cancellationToken);

    public async Task<ResultadoFluxoMvp?> ObterPorIntegracaoAsync(Guid integrationId, string correlationId,
        string operation, CancellationToken ct)
    {
        _ = ResultadoFluxoMvp.MapearFluxo(operation);
        await using var command = dataSource.CreateCommand("""
            select p.* from fugates.projecao_resultado_fluxo_mvp p
            join fugates.integracao i on i.id = p.integracao_id
            where i.id = @integracao_id and i.correlation_id = @correlation_id and i.operacao = @operacao;
            """);
        Add(command, "integracao_id", DbType.Guid, integrationId);
        Add(command, "correlation_id", DbType.String, correlationId);
        Add(command, "operacao", DbType.String, operation);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var wire = reader.GetString(reader.GetOrdinal("status_funcional"));
        var status = Enum.GetValues<StatusFluxoMvp>().SingleOrDefault(s => StatusWire(s) == wire);
        if (StatusWire(status) != wire) throw new InvalidOperationException("MVP_STORED_STATUS_INVALID");
        string? Text(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
        var result = new ResultadoFluxoMvp(status, correlationId, Text("etapa_funcional")!,
            DecisaoPendente: Text("decisao_funcional_pendente"), ResultadoFuncional: Text("resultado_funcional"),
            SalesOrder: Text("sales_order"), DeliveryDocument: Text("delivery_document"),
            PurchaseOrder: Text("purchase_order"), MaterialDocument: Text("material_document"),
            MaterialDocumentYear: Text("material_document_year"), ProtocoloGuardian: Text("protocolo_guardian"),
            NumeroTicketGuardian: Text("numero_ticket_guardian"), ErroCodigo: Text("erro_codigo"),
            ErroCategoria: Text("erro_categoria"), ErroEtapa: Text("erro_etapa"),
            ErroReprocessavel: reader.IsDBNull(reader.GetOrdinal("erro_reprocessavel")) ? null : reader.GetBoolean(reader.GetOrdinal("erro_reprocessavel")),
            ErroResultadoIndeterminado: reader.GetBoolean(reader.GetOrdinal("erro_resultado_indeterminado")));
        if (Text("fluxo") != ResultadoFluxoMvp.MapearFluxo(operation) ||
            result.ErroResultadoIndeterminado != (status == StatusFluxoMvp.ResultadoIndeterminado))
            throw new InvalidOperationException("MVP_STORED_RESULT_INVALID");
        return result.Normalizar();
    }

    public Task<bool> PersistirAsync(ClaimIntegracaoMotor claim, ResultadoFluxoMvp resultado, CancellationToken cancellationToken)
        => PersistirCoreAsync(claim,resultado,cancellationToken,false,null);
    public Task<bool> ReservarEtapaAsync(ClaimIntegracaoMotor claim,string? etapaAnterior,ResultadoFluxoMvp preparado,CancellationToken ct)
        => PersistirCoreAsync(claim,preparado,ct,true,etapaAnterior);
    private async Task<bool> PersistirCoreAsync(ClaimIntegracaoMotor claim, ResultadoFluxoMvp resultado, CancellationToken cancellationToken,bool comparar,string? etapaAnterior)
    {
        var fluxo = ResultadoFluxoMvp.MapearFluxo(claim.Operacao);
        resultado = resultado.Normalizar();
        if (resultado.Correlacao != claim.CorrelationId) return false;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var locked = connection.CreateCommand();
            locked.Transaction = transaction;
            locked.CommandText = LockClaimSql;
            AddClaim(locked, claim);
            if (await locked.ExecuteScalarAsync(cancellationToken) is not Guid id || id != claim.IntegracaoId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
            if(comparar)
            {
                await using var state=connection.CreateCommand();state.Transaction=transaction;
                state.CommandText="select etapa_funcional from fugates.projecao_resultado_fluxo_mvp where integracao_id=@integracao_id for update;";
                Add(state,"integracao_id",DbType.Guid,claim.IntegracaoId);
                var previous=await state.ExecuteScalarAsync(cancellationToken);
                var actual=previous is null or DBNull ? null : Convert.ToString(previous);
                if(actual!=etapaAnterior) { await transaction.RollbackAsync(cancellationToken);return false; }
            }
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = UpsertSql;
            AddClaim(upsert, claim);
            Add(upsert, "fluxo", DbType.String, fluxo);
            Add(upsert, "status_funcional", DbType.String, StatusWire(resultado.Status));
            foreach (var (name, value) in new (string, string?)[] {
                ("etapa_funcional", resultado.Etapa), ("resultado_funcional", resultado.ResultadoFuncional),
                ("decisao_funcional_pendente", resultado.DecisaoPendente), ("sales_order", resultado.SalesOrder),
                ("delivery_document", resultado.DeliveryDocument), ("purchase_order", resultado.PurchaseOrder),
                ("material_document", resultado.MaterialDocument), ("material_document_year", resultado.MaterialDocumentYear),
                ("protocolo_guardian", resultado.ProtocoloGuardian), ("numero_ticket_guardian", resultado.NumeroTicketGuardian),
                ("erro_codigo", resultado.ErroCodigo), ("erro_categoria", resultado.ErroCategoria), ("erro_etapa", resultado.ErroEtapa) })
                Add(upsert, name, DbType.String, value);
            Add(upsert, "erro_reprocessavel", DbType.Boolean, resultado.ErroReprocessavel);
            Add(upsert, "erro_resultado_indeterminado", DbType.Boolean, resultado.ErroResultadoIndeterminado);
            var accepted = await upsert.ExecuteScalarAsync(cancellationToken) is Guid persisted && persisted == claim.IntegracaoId;
            if (!accepted) { await transaction.RollbackAsync(cancellationToken); return false; }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void AddClaim(DbCommand command, ClaimIntegracaoMotor claim)
    {
        Add(command, "integracao_id", DbType.Guid, claim.IntegracaoId);
        Add(command, "tentativa_id", DbType.Guid, claim.TentativaId);
        Add(command, "fencing_token", DbType.Int64, claim.FencingToken);
        Add(command, "worker", DbType.String, claim.Worker);
        Add(command, "correlation_id", DbType.String, claim.CorrelationId);
        Add(command, "operacao", DbType.String, claim.Operacao);
    }

    private static void Add(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
