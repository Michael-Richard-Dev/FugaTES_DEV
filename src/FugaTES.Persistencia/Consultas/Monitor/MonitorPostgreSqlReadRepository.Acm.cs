using FugaTES.Aplicacao.Consultas.Monitor;
using Npgsql;

namespace FugaTES.Persistencia.Consultas.Monitor;

public sealed partial class MonitorPostgreSqlReadRepository
{
    public const string SqlIdentidadeAcm = """
        select coalesce(current_database()='fugates_dev'
          and current_user='fugates_acm_hml_app'
          and current_setting('port')='5433'
          and current_setting('cluster_name',true)='18/fugatesacmdev'
          and current_setting('server_version_num')::integer between 180000 and 189999
          and current_setting('transaction_read_only')='on'
          and inet_server_addr()=inet '127.0.0.1'
          and exists(select 1 from pg_namespace where nspname='fugates'),false);
        """;

    // R166: identico ao predicado acima em TODAS as condicoes fisicas; a unica diferenca e que a
    // role esperada entra por parametro (@role), em vez de literal. Existe porque a API privada
    // 18762 passou a ter identidade GET-only propria (fugates_dev_read_app): sem isto o guard
    // recusa a conexao e o processo encerra com UNIFIED_DEV_DATABASE_IDENTITY_REJECTED.
    // Nao afrouxa nada: quem nao informa role continua usando SqlIdentidadeAcm, fixo na role
    // compartilhada, e o valor de @role e validado contra a lista branca em RolesDeLeituraAutorizadas.
    public const string SqlIdentidadeRole = """
        select coalesce(current_database()='fugates_dev'
          and current_user=@role
          and current_setting('port')='5433'
          and current_setting('cluster_name',true)='18/fugatesacmdev'
          and current_setting('server_version_num')::integer between 180000 and 189999
          and current_setting('transaction_read_only')='on'
          and inet_server_addr()=inet '127.0.0.1'
          and exists(select 1 from pg_namespace where nspname='fugates'),false);
        """;

    public const string RoleAcmCompartilhada = "fugates_acm_hml_app";
    public const string RoleDevSomenteLeitura = "fugates_dev_read_app";

    // Lista branca das UNICAS identidades que podem servir leitura. A role de escrita do receptor
    // SAP (fugates_sap_inbound_app) NAO esta aqui, de proposito.
    public static readonly string[] RolesDeLeituraAutorizadas = [RoleAcmCompartilhada, RoleDevSomenteLeitura];

    // Devolve o comando de identidade correto, com a role ja ligada como parametro.
    public static NpgsqlCommand ComandoIdentidade(string? roleEsperada, NpgsqlConnection c, NpgsqlTransaction tx)
    {
        if (roleEsperada is null)
            return new NpgsqlCommand(SqlIdentidadeAcm, c, tx) { CommandTimeout = 5 };
        if (!RolesDeLeituraAutorizadas.Contains(roleEsperada))
            throw new InvalidOperationException("ACM_DATABASE_IDENTITY_ROLE_NOT_AUTHORIZED");
        var command = new NpgsqlCommand(SqlIdentidadeRole, c, tx) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("role", roleEsperada);
        return command;
    }

    // Only scalar identifiers and evidence-presence flags cross this read boundary.
    public static string SqlContratoAcm(bool porIntegracao) => $$"""
        select row_to_json(m)::text from (
          select v.numero_contrato as "Contrato", v.versao as "Versao",
            v.payload_contrato->>'Centro' as "Centro", v.criado_em as "RecebidoEm",
            to_jsonb(v)->>'guardian_codigo_confirmado' as "CodigoGuardian",
            to_jsonb(v)->>'guardian_numero_referencia' as "Referencia",
            to_jsonb(v)->>'guardian_protocolo_id' as "Protocolo",
            to_jsonb(v)->>'guardian_callback_sequencial' as "Sequencial"
          from fugates.contrato_sap_toledo_versao v
          where {{(porIntegracao ? "v.integracao_id=@id" : "to_jsonb(v)->>'guardian_protocolo_id'=@protocol")}}
          order by v.versao desc limit 2
        ) m;
        """;

    public static string SqlCorrelacaoAcm(bool eventos, bool projecao) => $$"""
        select row_to_json(m)::text from (
          select c.protocolo_guardian as "Protocolo", c.ticket_guardian as "Sequencial",
            to_jsonb(c)->>'guardian_callback_id_original' as "CallbackIdOriginal",
            to_jsonb(c)->>'guardian_ticket_codigo' as "TicketCodigo",
            (nullif(to_jsonb(c)->>'guardian_ticket_evidence_json','') is not null
              and nullif(to_jsonb(c)->>'guardian_ticket_evidence_sha256','') is not null
              and nullif(to_jsonb(c)->>'guardian_ticket_evidence_received_at','') is not null) as "EvidenciaCompleta",
            (to_jsonb(c)->>'guardian_ticket_evidence_received_at')::timestamptz as "EvidenciaRecebidaEm"
          from fugates.correlacao_guardian_sap c
          where (@protocol is not null and c.protocolo_guardian=@protocol)
            {{(eventos ? "or c.id in (select e.correlacao_id from fugates.correlacao_guardian_evento e where e.integracao_id=@id)" : "")}}
            {{(projecao ? "or c.protocolo_guardian in (select p.protocolo_guardian from fugates.projecao_resultado_fluxo_mvp p where p.integracao_id=@id)" : "")}}
          order by c.id limit 2
        ) m;
        """;

    private static async Task<bool> TemColunasAsync(string tabela, string[] colunas, NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            select count(*)=@count from pg_attribute
            where attrelid=to_regclass(@table) and attnum>0 and not attisdropped and attname=any(@columns);
            """, connection, tx) { CommandTimeout = 5 };
        cmd.Parameters.AddWithValue("count", colunas.Length); cmd.Parameters.AddWithValue("table", tabela);
        cmd.Parameters.AddWithValue("columns", colunas);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<MonitorAcmDados> LerAcmAsync(Guid id, NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        async Task<IReadOnlyList<MonitorAcmDados>> Read(string sql, string? protocol = null)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, tx) { CommandTimeout = 10 };
            cmd.Parameters.AddWithValue("id", id); Texto(cmd, "protocol", protocol);
            return await LerAsync<MonitorAcmDados>(cmd, ct);
        }
        var h4 = await ExisteAsync("fugates.contrato_sap_toledo_versao", connection, tx, ct);
        var cor = await ExisteAsync("fugates.correlacao_guardian_sap", connection, tx, ct);
        var facts = new MonitorAcmDados
        {
            ContratosDisponiveis = h4, CorrelacaoDisponivel = cor,
            IdentidadesDisponiveis = cor && await TemColunasAsync("fugates.correlacao_guardian_sap", ["guardian_callback_id_original", "guardian_ticket_codigo"], connection, tx, ct),
            EvidenciaDisponivel = cor && await TemColunasAsync("fugates.correlacao_guardian_sap", ["guardian_ticket_evidence_json", "guardian_ticket_evidence_sha256", "guardian_ticket_evidence_received_at"], connection, tx, ct)
        };
        var contracts = h4 ? await Read(SqlContratoAcm(true)) : [];
        var contract = contracts.Count == 1 ? contracts[0] : null;
        var correlations = cor ? await Read(SqlCorrelacaoAcm(
            await ExisteAsync("fugates.correlacao_guardian_evento", connection, tx, ct),
            await ExisteAsync("fugates.projecao_resultado_fluxo_mvp", connection, tx, ct)), contract?.Protocolo) : [];
        if (contracts.Count > 1 || correlations.Count > 1) return facts with { CorrelacaoAmbigua = true };
        var correlation = correlations.SingleOrDefault();
        if (contract is null && h4 && correlation?.Protocolo is not null)
        {
            contracts = await Read(SqlContratoAcm(false), correlation.Protocolo);
            if (contracts.Count > 1) return facts with { CorrelacaoAmbigua = true };
            contract = contracts.SingleOrDefault();
        }
        if (contract?.Protocolo is not null && correlation?.Protocolo is not null && contract.Protocolo != correlation.Protocolo)
            return facts with { CorrelacaoAmbigua = true };
        return facts with
        {
            Contrato = contract?.Contrato, Versao = contract?.Versao, Centro = contract?.Centro,
            RecebidoEm = contract?.RecebidoEm, CodigoGuardian = contract?.CodigoGuardian, Referencia = contract?.Referencia,
            Protocolo = correlation?.Protocolo ?? contract?.Protocolo, Sequencial = correlation?.Sequencial ?? contract?.Sequencial,
            CallbackIdOriginal = correlation?.CallbackIdOriginal, TicketCodigo = correlation?.TicketCodigo,
            EvidenciaCompleta = correlation?.EvidenciaCompleta == true, EvidenciaRecebidaEm = correlation?.EvidenciaRecebidaEm
        };
    }
}
