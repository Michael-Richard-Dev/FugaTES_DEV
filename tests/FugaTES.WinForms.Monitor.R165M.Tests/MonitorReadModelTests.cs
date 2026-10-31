using System.Reflection;
using FugaTES.Persistencia.Consultas.Monitor;
using Npgsql;

namespace FugaTES.Testes;

public sealed class MonitorReadModelTests
{
    [Fact]
    public void Extra_identifiers_are_sanitized_without_normalizing_literal_ids()
    {
        var result = new MonitorAcmDados { Contrato = "0000001155", CodigoGuardian = "001155", Protocolo = "000300", Sequencial = "00029", CallbackIdOriginal = "0015", TicketCodigo = "Bearer SYNTHETIC" }.Sanitizar();
        Assert.Equal("0000001155", result.Contrato); Assert.Equal("001155", result.CodigoGuardian);
        Assert.Equal("000300", result.Protocolo); Assert.Equal("00029", result.Sequencial);
        Assert.Equal("0015", result.CallbackIdOriginal); Assert.Equal("[CONTEUDO OMITIDO]", result.TicketCodigo);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void Optional_correlation_queries_export_no_private_evidence_body(bool events, bool projection)
    {
        var sql = MonitorPostgreSqlReadRepository.SqlCorrelacaoAcm(events, projection);
        Assert.Equal(events, sql.Contains("correlacao_guardian_evento"));
        Assert.Equal(projection, sql.Contains("projecao_resultado_fluxo_mvp"));
        Assert.DoesNotContain("as \"EvidenciaJson\"", sql);
        Assert.Contains("limit 2", sql);
        foreach (var verb in new[] { "insert ", "update ", "delete ", "alter ", "create ", "drop " })
            Assert.DoesNotContain(verb, sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Existing_repository_can_require_physical_ACM_identity()
    {
        // R166: a assinatura ganhou a role esperada como terceiro parâmetro OPCIONAL. O default
        // continua sendo a identidade ACM compartilhada, então todo chamador histórico — inclusive
        // os que passam apenas (source, true) — mantém exatamente o comportamento anterior.
        var ctor = typeof(MonitorPostgreSqlReadRepository)
            .GetConstructor([typeof(NpgsqlDataSource), typeof(bool), typeof(string)]);
        Assert.NotNull(ctor);
        var parametros = ctor.GetParameters();
        Assert.True(parametros[1].HasDefaultValue && Equals(parametros[1].DefaultValue, false));
        Assert.True(parametros[2].HasDefaultValue && parametros[2].DefaultValue is null);

        var field = typeof(MonitorPostgreSqlReadRepository).GetField("SqlIdentidadeAcm");
        Assert.NotNull(field);
        var sql = Assert.IsType<string>(field.GetRawConstantValue());
        foreach (var value in new[] { "fugates_acm_hml_app", "5433", "18/fugatesacmdev", "transaction_read_only", "127.0.0.1" })
            Assert.Contains(value, sql);
    }

    // A variante parametrizada fixa TODAS as mesmas condições físicas; só a role sai do literal.
    [Fact]
    public void Parameterized_identity_pins_everything_except_the_role()
    {
        var sql = Assert.IsType<string>(
            typeof(MonitorPostgreSqlReadRepository).GetField("SqlIdentidadeRole")!.GetRawConstantValue());
        foreach (var value in new[] { "5433", "18/fugatesacmdev", "transaction_read_only", "127.0.0.1", "current_user=@role" })
            Assert.Contains(value, sql);
        // A role não pode voltar a ser literal nesta variante, nem a identidade deixa de ser exigida.
        Assert.DoesNotContain("fugates_acm_hml_app", sql);
        Assert.DoesNotContain("fugates_dev_read_app", sql);
        foreach (var verb in new[] { "insert ", "update ", "delete ", "alter ", "create ", "drop " })
            Assert.DoesNotContain(verb, sql, StringComparison.OrdinalIgnoreCase);
    }

    // Lista branca de leitura: exatamente as duas identidades GET. A role de escrita do receptor
    // SAP nunca pode servir leitura, e nenhuma outra identidade é aceita.
    [Theory]
    [InlineData("fugates_acm_hml_app", true)]
    [InlineData("fugates_dev_read_app", true)]
    [InlineData("fugates_sap_inbound_app", false)]
    [InlineData("fugates_dev_app", false)]
    [InlineData("fugates_dev_owner", false)]
    [InlineData("postgres", false)]
    public void Only_the_two_read_identities_are_authorized(string role, bool autorizada)
    {
        Assert.Equal(autorizada, MonitorPostgreSqlReadRepository.RolesDeLeituraAutorizadas.Contains(role));
        if (autorizada) return;
        // E uma role não autorizada é recusada ao montar o comando, antes de qualquer conexão.
        var erro = Assert.Throws<InvalidOperationException>(() =>
            MonitorPostgreSqlReadRepository.ComandoIdentidade(role, null!, null!));
        Assert.Equal("ACM_DATABASE_IDENTITY_ROLE_NOT_AUTHORIZED", erro.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Contract_query_uses_durable_links_and_exports_only_scalars(bool byIntegration)
    {
        var method = typeof(MonitorPostgreSqlReadRepository).GetMethod("SqlContratoAcm", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var sql = Assert.IsType<string>(method.Invoke(null, [byIntegration]));
        Assert.Contains(byIntegration ? "v.integracao_id=@id" : "guardian_protocolo_id'=@protocol", sql);
        Assert.Contains("guardian_codigo_confirmado", sql);
        Assert.Contains("limit 2", sql);
        Assert.DoesNotContain("lpad", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v.payload_contrato as", sql);
        foreach (var verb in new[] { "insert ", "update ", "delete ", "alter ", "create ", "drop " })
            Assert.DoesNotContain(verb, sql, StringComparison.OrdinalIgnoreCase);
    }
}
