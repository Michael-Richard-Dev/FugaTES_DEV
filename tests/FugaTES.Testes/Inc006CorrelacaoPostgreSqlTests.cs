using System.Reflection;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Callback.Http;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Persistencia.Recebimento;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class Inc006CorrelacaoPostgreSqlTests
{
    [Fact]
    public void Fingerprint_callback_usa_canonicalizacao_congelada()
    {
        var fingerprint = CallbackGuardianMvpFingerprint.Calcular(
            new CallbackGuardianMvp("PROTOCOLO-001", "  TICKET-001  ", 1200.00m, 200.0m, 1000m, " concluido "));

        Assert.Equal("98E6585E1AB1F600F3492EB89D737A09C73B99FEECB69CB917A1B9A67F082734", fingerprint);
    }

    [Fact]
    public async Task Mesmo_documento_e_snapshot_inclusive_pedido_compras_e_idempotente()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();

        var first = await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        var repeated = await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);

        Assert.Equal(StatusRegistroSnapshotMvp.Criada, first.Status);
        Assert.Equal(StatusRegistroSnapshotMvp.Idempotente, repeated.Status);
    }

    [Fact]
    public async Task Mesmo_documento_com_pedido_compras_diferente_e_conflito()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);

        var result = await repository.RegistrarSnapshotAsync(Input() with { PedidoCompras = "PO-OUTRO" }, CancellationToken.None);

        Assert.Equal(StatusRegistroSnapshotMvp.Conflito, result.Status);
    }

    [Fact]
    public async Task Retry_mesmo_protocolo_apos_ticket_associado_nao_regride_estado()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        await repository.AssociarProtocoloAsync("DOC-001", "PROTOCOLO-001", CancellationToken.None);
        await repository.RegistrarCallbackAsync(Callback(), CancellationToken.None);

        var retry = await repository.AssociarProtocoloAsync("DOC-001", "PROTOCOLO-001", CancellationToken.None);

        Assert.Equal(StatusAssociacaoProtocoloMvp.Idempotente, retry.Status);
        Assert.Equal(EstadoCorrelacaoGuardianSap.TicketAssociado, retry.Correlacao.Estado);
    }

    [Fact]
    public async Task Mesmo_ticket_e_fingerprint_e_idempotente_mas_fingerprint_diferente_e_conflito()
    {
        var repository = await RepositoryWithProtocolAsync();
        var first = await repository.RegistrarCallbackAsync(Callback(), CancellationToken.None);
        var repeated = await repository.RegistrarCallbackAsync(Callback(), CancellationToken.None);
        var conflicting = await repository.RegistrarCallbackAsync(Callback() with { PesoBruto = 1300m }, CancellationToken.None);

        Assert.Equal(StatusRegistroCallbackMvp.Novo, first.Status);
        Assert.Equal(StatusRegistroCallbackMvp.Idempotente, repeated.Status);
        Assert.Equal(StatusRegistroCallbackMvp.Conflito, conflicting.Status);
        Assert.Equal(first.Correlacao.CallbackFingerprint, conflicting.Correlacao.CallbackFingerprint);
    }

    [Fact]
    public async Task Contexto_reidratado_preenche_tipo_documento_e_pedido_compras_da_pesagem()
    {
        var repository = await RepositoryWithProtocolAsync();
        var sap = new PesagemSapMvpGatewayFake();
        var useCase = new ProcessarCallbackGuardianMvpCasoDeUso(repository, sap);

        var result = await useCase.ExecutarAsync(Callback(), CancellationToken.None);

        Assert.Equal("NF", result.Pesagem!.TipoDocumento);
        Assert.Equal("PO-001", result.Pesagem.PedidoCompras);
        Assert.Equal("DOC-001", result.Pesagem.DocumentoSap);
        Assert.Equal((short)7, result.Pesagem.TipoOperacao);
        Assert.Equal("ACM-001", result.Pesagem.ContratoACM);
    }

    [Fact]
    public void Provider_postgresql_resolve_repository_sem_abrir_conexao()
    {
        var services = new ServiceCollection();
        GuardianCallbackHttpComposition.ConfigurarServicos(services, Configuration("PostgreSql", "Host=localhost;Database=fugates;Username=test;Password=not-real"));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<RepositorioCorrelacaoGuardianSapPostgreSql>(provider.GetRequiredService<IRepositorioCorrelacaoRecebimento>());
        Assert.DoesNotContain(provider.GetServices<IRepositorioCorrelacaoRecebimento>(), x => x is RepositorioCorrelacaoRecebimentoMemoria);
    }

    [Fact]
    public void Provider_postgresql_sem_connection_string_falha_fechado()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GuardianCallbackHttpComposition.ConfigurarServicos(new ServiceCollection(), Configuration("PostgreSql", null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    public void Provider_ausente_ou_desconhecido_falha_fechado(string? provider)
    {
        Assert.Throws<InvalidOperationException>(() =>
            GuardianCallbackHttpComposition.ConfigurarServicos(new ServiceCollection(), Configuration(provider, null)));
    }

    [Fact]
    public void Repository_sql_e_integralmente_parametrizado()
    {
        var fields = typeof(RepositorioCorrelacaoGuardianSapPostgreSql)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.Contains("fugates.correlacao_guardian_sap", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(fields);
        Assert.All(fields, sql => Assert.DoesNotContain("$\"", sql, StringComparison.Ordinal));
        Assert.Contains(fields, sql => sql.Contains("@pedido_compras_sap", StringComparison.Ordinal));
        Assert.Contains(fields, sql => sql.Contains("@callback_fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public void Migration_rollback_e_validation_materializam_contrato_de_20_colunas()
    {
        var root = FindRoot();
        var migration = File.ReadAllText(Path.Combine(root, "docs", "Incrementos", "006", "MIGRATION_006F2_CORRELACAO_GUARDIAN_SAP.sql"));
        var rollback = File.ReadAllText(Path.Combine(root, "docs", "Incrementos", "006", "ROLLBACK_006F2_CORRELACAO_GUARDIAN_SAP.sql"));
        var validation = File.ReadAllText(Path.Combine(root, "docs", "Incrementos", "006", "VALIDATION_006F2_CORRELACAO_GUARDIAN_SAP.sql"));

        Assert.Contains("pedido_compras_sap text null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contrato_sap text not null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operacao_contrato text not null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo_operacao_sap smallint not null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contrato_acm_sap text null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("callback_fingerprint text null", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("if not exists", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drop table fugates.correlacao_guardian_sap", rollback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("column_count", validation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("20", validation, StringComparison.Ordinal);
    }

    private static async Task<RepositorioCorrelacaoRecebimentoMemoria> RepositoryWithProtocolAsync()
    {
        var repository = new RepositorioCorrelacaoRecebimentoMemoria();
        await repository.RegistrarSnapshotAsync(Input(), CancellationToken.None);
        await repository.AssociarContratoAcmAsync("DOC-001", "ACM-001", CancellationToken.None);
        await repository.AssociarProtocoloAsync("DOC-001", "PROTOCOLO-001", CancellationToken.None);
        return repository;
    }

    private static RecebimentoSapCpiMvp Input() => new(
        "RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-001", "BP-E", "BP-T", "CONTRATO-001", "DOC-001",
        "COMPRA", "CENTRO-001", "ENTRADA", "NF", "PO-001", 7, "ACM-001");

    private static CallbackGuardianMvp Callback() => new("PROTOCOLO-001", "TICKET-001", 1200m, 200m, 1000m, "CONCLUIDO");

    private static IConfiguration Configuration(string? provider, string? connectionString)
    {
        var values = new Dictionary<string, string?>
        {
            ["Callback:Enabled"] = "true",
            ["Callback:Route"] = GuardianCallbackEndpoint.Route,
            ["Correlation:Provider"] = provider,
            ["ConnectionStrings:FugaTES"] = connectionString
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "FugaTES.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("FugaTES root not found.");
    }
}
