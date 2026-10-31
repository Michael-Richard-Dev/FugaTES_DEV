using FugaTES.Aplicacao.Configuracao;
using Npgsql;
using NpgsqlTypes;

namespace FugaTES.Persistencia.MotorIntegracao;

/// <summary>One parameterized guard reused by selection and both transactional claim statements.</summary>
public static class AcmInboundWindowSqlScope
{
    public static string Apply(string sql, string terminator, MotorIntegracaoOptions? options)
    {
        var predicate = Predicate(options);
        if (predicate.Length == 0) return sql;
        var first = sql.IndexOf(terminator, StringComparison.Ordinal);
        if (first < 0 || sql.IndexOf(terminator, first + terminator.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("R165AG_WINDOW_SQL_GUARD_INSERTION_FAILED");
        return sql.Insert(first, predicate + "\n");
    }
    public static string Predicate(MotorIntegracaoOptions? options)
    {
        if (options?.UsaJanelaAcm != true) return string.Empty;
        if (!options.EhValida()) throw new InvalidOperationException("R165AG_ADMISSION_WINDOW_INVALID");
        return """
             and i.origem = 'SAP' and i.destino = 'TOLEDO' and i.operacao = 'SAP_CONTRATO_TOLEDO'
             and clock_timestamp() >= @window_from and clock_timestamp() < @window_until
             and i.criado_em >= @window_from and i.criado_em < @window_until
             and left(i.correlation_id, length(@window_prefix)) = @window_prefix
             and length(i.correlation_id) = length(@window_prefix) + 32
             and substring(i.correlation_id from length(@window_prefix) + 1) ~ '^[0-9a-fA-F]{32}$'
             and substring(i.correlation_id from length(@window_prefix) + 1) <> '00000000000000000000000000000000'
             and ltrim(i.chave_negocio, '0') <> all(@window_excluded)
             and exists (
                 select 1 from contrato_sap_toledo_versao w
                 where w.integracao_id = i.id and w.versao = 1
                   and w.numero_contrato = i.chave_negocio and w.fingerprint_sha256 = i.fingerprint_sha256
                   and w.payload_contrato->>'NumeroContrato' = i.chave_negocio
                   and w.payload_contrato->>'Centro' = @window_centro
                   and (w.payload_contrato->>'Item') is not distinct from @window_item
                   -- R170: mesmas duas combinacoes de AcmInboundWindowOptions.EscopoContratoAprovado,
                   -- como pares fechados de tipo + pesagem. A operacao do SAP nao participa: ela
                   -- varia para o mesmo sentido de negocio. Compra credita, venda debita.
                   and (   (w.payload_contrato->>'TipoContrato' = 'AC3P'
                        and w.payload_contrato->>'TipoPesagem' = 'E')
                        or (w.payload_contrato->>'TipoContrato' = 'AC3S'
                        and w.payload_contrato->>'TipoPesagem' = 'S'))
                   and w.payload_contrato->>'StatusContrato' = 'T'
                   and w.payload_contrato->>'IdParceiro' = any(@window_partners)
                   and w.payload_contrato->>'Material' = any(@window_materials)
             )
            """;
    }

    public static void AddParameters(NpgsqlCommand command, MotorIntegracaoOptions? options)
    {
        if (options?.UsaJanelaAcm != true) return;
        _ = Predicate(options);
        var w = options.AcmInboundWindow;
        command.Parameters.AddWithValue("window_from", w.NotBeforeUtc!.Value);
        command.Parameters.AddWithValue("window_until", w.UntilUtc!.Value);
        command.Parameters.AddWithValue("window_prefix", w.CorrelationPrefix);
        command.Parameters.AddWithValue("window_centro", w.Centro!);
        command.Parameters.AddWithValue("window_item", NpgsqlDbType.Text, (object?)w.Item ?? DBNull.Value);
        command.Parameters.AddWithValue("window_partners", w.AllowedSapPartners);
        command.Parameters.AddWithValue("window_materials", w.AllowedSapMaterials);
        command.Parameters.AddWithValue("window_excluded", new[] { "1031", "1106", "1138", "1139", "1140", "1144", "1147", "1155" }
            .Concat(w.ExcludedContractNumbers).Select(n => n.TrimStart('0')).Distinct(StringComparer.Ordinal).ToArray());
    }
}
