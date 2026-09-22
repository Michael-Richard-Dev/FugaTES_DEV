using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.HostIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace FugaTES.Testes;

public sealed class Inc005P1DPersistenciaPostgreSqlTests
{
    [Fact]
    public void Host_PersistenceModeFake_ResolveRepositorioFake()
    {
        using var host = CriarHost(ConfigValida());

        Assert.IsType<RepositorioMotorIntegracaoFake>(host.Services.GetRequiredService<IMotorIntegracaoRepository>());
    }

    [Fact]
    public void Host_PersistenceModePostgreSql_ResolveRepositorioPostgreSqlSemAbrirConexao()
    {
        var config = ConfigValida();
        config["HostIntegracao:PersistenceMode"] = "PostgreSql";
        config["ConnectionStrings:FugaTES"] = "Host=example.invalid;Database=fugates;Username=fugates";
        using var host = CriarHost(config);

        Assert.IsType<RepositorioMotorIntegracaoPostgreSql>(host.Services.GetRequiredService<IMotorIntegracaoRepository>());
    }

    [Fact]
    public void Host_PersistenceModePostgreSql_SemConnectionString_FalhaFechadoSemVazarSegredo()
    {
        var config = ConfigValida();
        config["HostIntegracao:PersistenceMode"] = "PostgreSql";

        var excecao = Assert.ThrowsAny<Exception>(() => CriarHost(config).Services.GetRequiredService<IMotorIntegracaoRepository>());

        Assert.Contains("ConnectionStrings:FugaTES", excecao.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Password", excecao.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", excecao.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Host_PersistenceModeInvalido_FalhaFechado()
    {
        var config = ConfigValida();
        config["HostIntegracao:PersistenceMode"] = "Memoria";

        Assert.ThrowsAny<Exception>(() => CriarHost(config).StartAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void Host_ExternalModeIndependenteDePersistenceMode()
    {
        var config = ConfigValida();
        config["HostIntegracao:PersistenceMode"] = "PostgreSql";
        config["HostIntegracao:ExternalMode"] = "Fake";
        config["ConnectionStrings:FugaTES"] = "Host=example.invalid;Database=fugates;Username=fugates";
        using var host = CriarHost(config);

        Assert.IsType<RepositorioMotorIntegracaoPostgreSql>(host.Services.GetRequiredService<IMotorIntegracaoRepository>());
        Assert.IsType<ProcessadorExternoIntegracaoFake>(host.Services.GetRequiredService<IProcessadorExternoIntegracao>());
    }

    [Fact]
    public void RepositoryPostgreSql_ImplementaInterfaceExistente()
    {
        Assert.True(typeof(IMotorIntegracaoRepository).IsAssignableFrom(typeof(RepositorioMotorIntegracaoPostgreSql)));
    }

    [Fact]
    public void RepositoryPostgreSql_UsaNpgsqlDataSourceENaoConnectionStringDireta()
    {
        var fonte = FonteRepositorio();

        Assert.Contains("NpgsqlDataSource", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("GetConnectionString", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("Pass" + "word=", fonte, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_ClaimEspecificoSemCteCrossStatement()
    {
        var claim = SqlClaim();
        var select = claim.IndexOf("from integracao i", StringComparison.OrdinalIgnoreCase);
        var locked = claim.IndexOf("for update skip locked", StringComparison.OrdinalIgnoreCase);
        var insert = claim.IndexOf("insert into tentativa_integracao", StringComparison.OrdinalIgnoreCase);
        var update = claim.IndexOf("update integracao", StringComparison.OrdinalIgnoreCase);
        var tentativaAtual = claim.IndexOf("tentativa_atual_id", StringComparison.OrdinalIgnoreCase);

        Assert.True(select >= 0);
        Assert.True(locked > select);
        Assert.True(insert > locked);
        Assert.True(update > insert);
        Assert.True(tentativaAtual > update);
        Assert.Contains(":integracao_id", claim, StringComparison.Ordinal);
        Assert.DoesNotContain("with alvo as", claim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("from alvo", claim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("order by", claim, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limit 1", claim, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_WritesFencedUsamPredicadosObrigatorios()
    {
        var fonte = FonteRepositorio();

        foreach (var marcador in new[]
        {
            "i.id = @integracao_id",
            "i.tentativa_atual_id = @tentativa_id",
            "i.fencing_token = @fencing_token",
            "i.worker_proprietario = @worker",
            "i.estado = 'EM_PROCESSAMENTO'",
            "i.lease_expira_em > clock_timestamp()"
        })
        {
            Assert.Contains(marcador, fonte, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RepositoryPostgreSql_ResultadoIndeterminadoNaoAgendaRetry()
    {
        var fonte = FonteRepositorio();
        var metodo = TrechoMetodo(fonte, "RegistrarResultadoIndeterminadoAsync");

        Assert.Contains("estado = 'RESULTADO_INDETERMINADO'", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo_disparo_pendente = null", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proxima_tentativa_em = null", metodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_ReprocessarNaoAceitaResultadoIndeterminado()
    {
        var metodo = TrechoMetodo(FonteRepositorio(), "ReprocessarAsync");

        Assert.DoesNotContain("RESULTADO_INDETERMINADO", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AGUARDANDO_ACAO_MANUAL", metodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_PersistirResultadoFechaTentativaEIntegracaoNaMesmaTransacao()
    {
        var metodo = TrechoMetodo(FonteRepositorio(), "PersistirResultadoAsync");

        Assert.Contains("BeginTransactionAsync", metodo, StringComparison.Ordinal);
        Assert.Contains("update tentativa_integracao", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update integracao", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CommitAsync", metodo, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", metodo, StringComparison.Ordinal);
        Assert.Contains("fase_envio = 'RESULTADO_RECEBIDO'", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resultado_recebido_em = clock_timestamp()", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("desfecho = @desfecho_tentativa", metodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_PersistirResultadoConhecidoMapeiaSucessoFalhaSemIndeterminado()
    {
        var metodo = TrechoMetodo(FonteRepositorio(), "PersistirResultadoAsync");

        Assert.Contains("resultado.Falha is null", metodo, StringComparison.Ordinal);
        Assert.Contains("\"SUCESSO\"", metodo, StringComparison.Ordinal);
        Assert.Contains("\"FALHA\"", metodo, StringComparison.Ordinal);
        Assert.Contains("resultado.Falha == ClassificacaoFalhaIntegracao.ResultadoIndeterminado", metodo, StringComparison.Ordinal);
        Assert.Contains("return false", metodo, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryPostgreSql_RegistrarResultadoIndeterminadoFechaTentativaSemRetry()
    {
        var metodo = TrechoMetodo(FonteRepositorio(), "RegistrarResultadoIndeterminadoAsync");

        Assert.Contains("BeginTransactionAsync", metodo, StringComparison.Ordinal);
        Assert.Contains("update tentativa_integracao", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("desfecho = 'INDETERMINADA'", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("estado = 'RESULTADO_INDETERMINADO'", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo_disparo_pendente = null", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proxima_tentativa_em = null", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CommitAsync", metodo, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", metodo, StringComparison.Ordinal);
        Assert.DoesNotContain("fase_envio = 'RESULTADO_RECEBIDO'", metodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_RecoveryFechaTentativaEIntegracaoAtomicamente()
    {
        var metodo = TrechoMetodo(FonteRepositorio(), "RecuperarLeasesExpiradasAsync");

        Assert.Contains("BeginTransactionAsync", metodo, StringComparison.Ordinal);
        Assert.Contains("update tentativa_integracao", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update integracao", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CommitAsync", metodo, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", metodo, StringComparison.Ordinal);
        Assert.Contains("INTERROMPIDA", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INDETERMINADA", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fase_envio = 'NAO_INICIADO'", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fase_envio = 'ENVIO_INICIADO'", metodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_RecoveryPreservaSemRetryParaPostMarker()
    {
        var metodo = TrechoMetodo(FonteRepositorio(), "RecuperarLeasesExpiradasAsync");

        Assert.Contains("estado = case when i.fase_envio = 'NAO_INICIADO' then 'AGUARDANDO_REPROCESSAMENTO' else 'RESULTADO_INDETERMINADO' end", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tipo_disparo_pendente = case when i.fase_envio = 'NAO_INICIADO' then 'AUTOMATICO' else null end", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proxima_tentativa_em = case when i.fase_envio = 'NAO_INICIADO' then clock_timestamp() else null end", metodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_RecoveryNaoIntroduzDdlRuntime()
    {
        var metodo = TrechoMetodo(FonteRepositorio(), "RecuperarLeasesExpiradasAsync");

        Assert.DoesNotContain("create table", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter table", metodo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", metodo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryPostgreSql_LifetimesPreservamDataSourceSingletonERepositorioNaoSingleton()
    {
        var fonte = File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "src", "FugaTES.Persistencia", "MotorIntegracao", "ServiceCollectionPersistenciaPostgreSqlExtensions.cs"));

        Assert.Contains("AddSingleton<NpgsqlDataSource>", fonte, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSingleton<RepositorioMotorIntegracaoPostgreSql>", fonte, StringComparison.Ordinal);
        Assert.Contains("AddTransient<RepositorioMotorIntegracaoPostgreSql>", fonte, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryPostgreSql_UsaClockDbParaLeaseEAgenda()
    {
        var fonte = FonteRepositorio();

        Assert.Contains("clock_timestamp()", fonte, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DateTime.UtcNow", fonte, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryPostgreSql_PortouCatalogoCompletoDeAuditoria()
    {
        var fonte = FonteRepositorio();
        foreach (var evento in new[] { "VALIDACAO_REPROVADA", "claim", "tentativa_criada", "marker_rejeitado", "envio_iniciado", "lease_renew_rejeitado", "lease_renew", "stale_writer_rejeitado", "resultado", "retry_agendado", "terminal", "indeterminado_no_caminho_generico_rejeitado", "indeterminado_rejeitado", "resultado_indeterminado", "reprocessar", "integrar_agora", "pausa", "retomada", "recovery" })
            Assert.Contains($"\"{evento}\"", fonte, StringComparison.Ordinal);
        Assert.Contains("@diagnostico", fonte, StringComparison.Ordinal);
        Assert.Contains("diagnosticoSanitizado", TrechoMetodo(fonte, "RegistrarValidacaoReprovadaAsync"), StringComparison.Ordinal);

        var resultado = TrechoMetodo(fonte, "PersistirResultadoAsync");
        Assert.True(resultado.IndexOf("\"resultado\"", StringComparison.Ordinal)
            < resultado.IndexOf("agendaRetry ? \"retry_agendado\" : \"terminal\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ClaimarEspecificamenteAsync", "\"claim\"")]
    [InlineData("MarcarEnvioIniciadoAsync", "\"envio_iniciado\"")]
    [InlineData("RenovarLeaseAsync", "\"lease_renew\"")]
    [InlineData("PersistirResultadoAsync", "\"resultado\"")]
    [InlineData("RegistrarResultadoIndeterminadoAsync", "\"resultado_indeterminado\"")]
    [InlineData("ReprocessarAsync", "\"reprocessar\"")]
    [InlineData("IntegrarAgoraAsync", "\"integrar_agora\"")]
    [InlineData("CriarPausaAsync", "\"pausa\"")]
    [InlineData("RetomarPausaAsync", "\"retomada\"")]
    [InlineData("RecuperarLeasesExpiradasAsync", "\"recovery\"")]
    public void RepositoryPostgreSql_AuditoriaCompartilhaTransacaoDaMutacao(string metodo, string evento)
    {
        var trecho = TrechoMetodo(FonteRepositorio(), metodo);
        Assert.Contains("BeginTransactionAsync", trecho, StringComparison.Ordinal);
        Assert.Contains("RegistrarAuditoriaAsync", trecho, StringComparison.Ordinal);
        Assert.Contains("RegistrarAuditoriaAsync(connection, transaction", trecho, StringComparison.Ordinal);
        Assert.Contains(evento, trecho, StringComparison.Ordinal);
        Assert.True(trecho.IndexOf(evento, StringComparison.Ordinal) < trecho.IndexOf("CommitAsync", StringComparison.Ordinal));
    }

    private static IHost CriarHost(Dictionary<string, string?> config)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Configuration.AddInMemoryCollection(config);
        HostIntegracaoComposition.Configurar(builder);
        return builder.Build();
    }

    private static Dictionary<string, string?> ConfigValida()
    {
        return new Dictionary<string, string?>
        {
            ["HostIntegracao:PollInterval"] = "00:00:01",
            ["HostIntegracao:ShutdownGracePeriod"] = "00:00:05",
            ["HostIntegracao:ExternalMode"] = "Fake",
            ["HostIntegracao:PersistenceMode"] = "Fake",
            ["MotorIntegracao:LeaseDuration"] = "00:01:00",
            ["MotorIntegracao:LeaseRenewInterval"] = "00:00:20",
            ["MotorIntegracao:RetryBackoff"] = "00:05:00",
            ["TimeoutProcessamentoIntegracao:Timeout"] = "00:00:45"
        };
    }

    private static string FonteRepositorio()
    {
        return File.ReadAllText(Path.Combine(LocalizarRaizRepositorio(), "src", "FugaTES.Persistencia", "MotorIntegracao", "RepositorioMotorIntegracaoPostgreSql.cs"));
    }

    private static string SqlClaim()
    {
        var fonte = FonteRepositorio();
        return TrechoMetodo(fonte, "ClaimarEspecificamenteAsync");
    }

    private static string TrechoMetodo(string fonte, string nome)
    {
        var inicio = fonte.IndexOf(nome, StringComparison.Ordinal);
        Assert.True(inicio >= 0);
        var proximoMetodo = fonte.IndexOf("public async Task", inicio + nome.Length, StringComparison.Ordinal);
        return proximoMetodo < 0 ? fonte[inicio..] : fonte[inicio..proximoMetodo];
    }

    private static string LocalizarRaizRepositorio()
    {
        var diretorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (diretorio is not null && !File.Exists(Path.Combine(diretorio.FullName, "FugaTES.slnx")))
        {
            diretorio = diretorio.Parent;
        }

        return diretorio?.FullName ?? AppContext.BaseDirectory;
    }
}
