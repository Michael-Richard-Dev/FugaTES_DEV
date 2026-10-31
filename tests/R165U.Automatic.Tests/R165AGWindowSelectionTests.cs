using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Persistencia.MotorIntegracao;
using Npgsql;
using Xunit;

public sealed class R165AGWindowSelectionTests
{
    [Fact]
    public async Task Unknown_new_contract_selected_but_same_contract_old_marker_and_backlog_left_untouched()
    {
        var f = new AutomaticFixture(); var admitted = f.Add("9765401").Version;
        var oldSameNumber = f.Add("9765401", s => s with { QuantidadeContrato = 100m }).Version; var historical = f.Add("1155").Version;
        var w = Window(); var repository = new RepositorioMotorIntegracaoFake();
        PilotAutomaticSelectionTests.Seed(repository, oldSameNumber);
        PilotAutomaticSelectionTests.Seed(repository, historical);
        repository.Semear(new(admitted.IntegracaoId, "SAP", "TOLEDO", "SAP_CONTRATO_TOLEDO", admitted.NumeroContrato,
            admitted.FingerprintSha256, w.CriarCorrelationId(), EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico,
            DateTimeOffset.UtcNow.AddMinutes(-1), 0, null, null, FaseEnvioIntegracao.NaoIniciado, null));
        var processor = new PilotAutomaticSelectionTests.RecordingProcessor(); var detector = new Detector();
        var motor = new MotorIntegracaoServico(repository, processor, detector, new ValidadorGenericoIntegracao(),
            new Identity(), new Timeout(), TimeProvider.System, MotorOptions(w), f.Versions);
        Assert.Equal(CicloMotorIntegracaoResultado.NenhumTrabalho, await motor.ExecutarPorIdAsync(oldSameNumber.IntegracaoId, default));
        Assert.Equal(CicloMotorIntegracaoResultado.Processado, await motor.ExecutarUmCicloAsync(default));
        Assert.Equal(admitted.IntegracaoId, Assert.Single(processor.Claims).IntegracaoId);
        Assert.Equal(EstadoIntegracao.Pendente, (await repository.ObterPorIdAsync(historical.IntegracaoId, default))!.Estado);
        Assert.Equal(EstadoIntegracao.Pendente, (await repository.ObterPorIdAsync(oldSameNumber.IntegracaoId, default))!.Estado);
        Assert.Equal(0, detector.Calls);
    }

    [Theory]
    [InlineData("expired")] [InlineData("version")] [InlineData("fingerprint")] [InlineData("scope")]
    public async Task Window_H4_identity_mismatch_never_claims(string invalid)
    {
        var f = new AutomaticFixture(); var v = f.Add("9765404").Version; var w = Window();
        var options = MotorOptions(w);
        if (invalid == "expired") { w.NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-10); w.UntilUtc = DateTimeOffset.UtcNow.AddMinutes(-1); }
        if (invalid == "version") f.Versions.Items[v.IntegracaoId] = v with { Versao = 2 };
        if (invalid == "fingerprint") f.Versions.Items[v.IntegracaoId] = v with { Snapshot = v.Snapshot with { QuantidadeContrato = 2m } };
        if (invalid == "scope") f.Versions.Items[v.IntegracaoId] = v with { Snapshot = v.Snapshot with { Centro = "9999" } };
        var repo = new RepositorioMotorIntegracaoFake();
        repo.Semear(new(v.IntegracaoId, "SAP", "TOLEDO", "SAP_CONTRATO_TOLEDO", v.NumeroContrato, v.FingerprintSha256,
            w.CriarCorrelationId(), EstadoIntegracao.Pendente, TipoDisparoPendente.Automatico,
            DateTimeOffset.UtcNow.AddMinutes(-1), 0, null, null, FaseEnvioIntegracao.NaoIniciado, null));
        var processor = new PilotAutomaticSelectionTests.RecordingProcessor();
        var motor = new MotorIntegracaoServico(repo, processor, new Detector(), new ValidadorGenericoIntegracao(),
            new Identity(), new Timeout(), TimeProvider.System, options, f.Versions);
        Assert.Equal(CicloMotorIntegracaoResultado.NenhumTrabalho, await motor.ExecutarUmCicloAsync(default));
        Assert.Empty(repo.Tentativas); Assert.Empty(processor.Claims);
    }

    [Fact]
    public void SQL_scope_is_parameterized_and_contains_time_H4_and_marker_guards()
    {
        var options = MotorOptions(Window()); var sql = AcmInboundWindowSqlScope.Predicate(options);
        Assert.Contains("i.criado_em >= @window_from", sql); Assert.Contains("i.criado_em < @window_until", sql);
        Assert.Contains("clock_timestamp() < @window_until", sql); Assert.Contains("@window_prefix", sql);
        Assert.Contains("w.versao = 1", sql); Assert.Contains("w.fingerprint_sha256 = i.fingerprint_sha256", sql);
        Assert.Contains("@window_partners", sql); Assert.DoesNotContain(options.AcmInboundWindow.WindowId!, sql);
        using var command = new NpgsqlCommand(); AcmInboundWindowSqlScope.AddParameters(command, options);
        Assert.Equal(options.AcmInboundWindow.CorrelationPrefix, command.Parameters["window_prefix"].Value);
        Assert.Equal(options.AcmInboundWindow.NotBeforeUtc, command.Parameters["window_from"].Value);
        foreach (var terminal in new[] { "order by i.criado_em;", "for update skip locked;", "returning i.id;" })
            Assert.Contains(sql, AcmInboundWindowSqlScope.Apply("where true\n" + terminal, terminal, options));
        Assert.Throws<InvalidOperationException>(() => AcmInboundWindowSqlScope.Apply("where true", "returning i.id;", options));
        Assert.Equal(string.Empty, AcmInboundWindowSqlScope.Predicate(new MotorIntegracaoOptions()));
    }

    [Fact]
    public void Enabled_window_never_falls_back_to_global_queue_and_cannot_coexist_with_nominal_scope()
    {
        var options = new MotorIntegracaoOptions
        {
            LeaseDuration = TimeSpan.FromMinutes(1), LeaseRenewInterval = TimeSpan.FromSeconds(10),
            RetryBackoff = TimeSpan.FromMinutes(1)
        };
        var property = typeof(MotorIntegracaoOptions).GetProperty("AcmInboundWindow");
        Assert.NotNull(property);
        property.SetValue(options, Window());
        Assert.True(options.UsaEscopoAcm);
        Assert.True(options.EhValida());
        options.AcmPilotNumeroContrato = "1155";
        options.AcmPilotCentro = "3011";
        Assert.False(options.EhValida());
        options.AcmPilotNumeroContrato = null;
        options.AcmPilotCentro = null;
        property.SetValue(options, new AcmInboundWindowOptions { Enabled = true });
        Assert.True(options.UsaEscopoAcm);
        Assert.False(options.EhValida());
    }

    internal static AcmInboundWindowOptions Window() => new()
    {
        Enabled = true, WindowId = "e7945f2709e44d609a9749e7c75cfe0a",
        AuthorizationReference = "SYNTHETIC_TEST_WINDOW_ONLY", Centro = "3011", Item = "10",
        NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-5), UntilUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        AllowedSapPartners = ["0020000051"], AllowedSapMaterials = ["7000000"]
    };

    internal static MotorIntegracaoOptions MotorOptions(AcmInboundWindowOptions w) => new()
    { LeaseDuration = TimeSpan.FromMinutes(1), LeaseRenewInterval = TimeSpan.FromSeconds(10), RetryBackoff = TimeSpan.FromMinutes(1), AcmInboundWindow = w };
    private sealed class Detector : IDetectorCandidatosIntegracao
    { public int Calls; public Task<IReadOnlyList<CandidatoIntegracao>> DetectarAsync(CancellationToken ct) { Calls++; return Task.FromResult<IReadOnlyList<CandidatoIntegracao>>([]); } }
    private sealed class Identity : IIdentidadeWorkerProvider
    { public IdentidadeWorkerIntegracao Obter() => new("SYNTHETIC_WINDOW_WORKER"); }
    private sealed class Timeout : IPoliticaTimeoutProcessamentoIntegracao
    { public TimeSpan ObterTimeout(ClaimIntegracaoMotor claim) => TimeSpan.FromSeconds(5); }
}
