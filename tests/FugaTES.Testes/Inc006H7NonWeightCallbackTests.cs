using System.Text;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using FugaTES.Persistencia.Recebimento;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using FugaTES.Infraestrutura.Recebimento;

namespace FugaTES.Testes;

public sealed class Inc006H7NonWeightCallbackTests
{
    [Fact]
    public void Postgres_repository_uses_parent_lock_ledger_identity_and_null_nonweight_fields()
    {
        var type = typeof(RepositorioEventoGuardianPostgreSql);
        string Sql(string field) => (string)type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.Contains("protocolo_guardian = @protocolo_guardian for update", Sql("LockParentSql"));
        Assert.Contains("correlacao_id = @correlacao_id", Sql("SelectEventSql"));
        Assert.Contains("sequencial_guardian = @sequencial_guardian", Sql("SelectEventSql"));
        Assert.Contains("estado_codigo = @estado_codigo", Sql("SelectEventSql"));
        Assert.Contains("ticket_guardian is null", Sql("AttachTicketSql"));
        Assert.Contains("@peso_bruto, @tara_final, @peso_liquido, @integracao_id", Sql("InsertEventSql"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task First_attach_and_identical_replay_are_acknowledged_without_Sap_work(int state)
    {
        var store = new RecordingEventStore();
        var first = await PostAsync($$"""{"ID":98765,"Sequencial":12345,"EstadoCodigo":{{state}}}""", store);
        var replay = await PostAsync($$"""{"ID":98765,"Sequencial":12345,"EstadoCodigo":{{state}}}""", store);

        Assert.Equal(200, first.Status);
        Assert.Equal("OK", first.Body);
        Assert.Equal("text/plain", first.ContentType);
        Assert.Equal(200, replay.Status);
        Assert.Equal("OK", replay.Body);
        Assert.Equal(1, store.Insertions);
        Assert.Equal(0, store.SapWork);
        Assert.Equal("98765", store.LookupProtocol);
        Assert.Equal("12345", store.AttachedTicket);
    }

    [Fact]
    public async Task PreCadastro_and_Encerrado_have_separate_ledger_identity_for_same_parent_and_ticket()
    {
        var store = new RecordingEventStore();
        var first = await PostAsync("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":1}""", store);
        var second = await PostAsync("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":3}""", store);

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(2, store.Insertions);
        Assert.Equal(0, store.SapWork);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Same_ticket_and_event_with_different_semantic_payload_conflicts(int state)
    {
        var store = new RecordingEventStore();
        await PostAsync($$"""{"ID":98765,"Sequencial":12345,"EstadoCodigo":{{state}},"PesoLiquido":"1.00"}""", store);

        var conflict = await PostAsync($$"""{"ID":98765,"Sequencial":12345,"EstadoCodigo":{{state}},"PesoLiquido":"2.00"}""", store);

        Assert.Equal(409, conflict.Status);
        Assert.Equal(1, store.Insertions);
        Assert.Equal(0, store.SapWork);
    }

    [Fact]
    public async Task Different_ticket_cannot_reattach_parent()
    {
        var store = new RecordingEventStore();
        await PostAsync("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":1}""", store);

        var conflict = await PostAsync("""{"ID":98765,"Sequencial":12346,"EstadoCodigo":3}""", store);

        Assert.Equal(409, conflict.Status);
        Assert.Equal("12345", store.AttachedTicket);
        Assert.Equal(1, store.Insertions);
    }

    [Theory]
    [InlineData("""{"ID":"98765","Sequencial":12345,"EstadoCodigo":1}""")]
    [InlineData("""{"ID":98765,"Sequencial":"12345","EstadoCodigo":1}""")]
    [InlineData("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":0}""")]
    [InlineData("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":5,"PesoLiquido":123.45}""")]
    [InlineData("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":1,"PesoLiquido":"1,00"}""")]
    [InlineData("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":1,"PesoLiquido":"1.0"}""")]
    [InlineData("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":1,"PesoLiquido":"-1.00"}""")]
    public async Task Invalid_wire_fails_before_store(string json)
    {
        var store = new RecordingEventStore();
        var response = await PostAsync(json, store);
        Assert.Equal(400, response.Status);
        Assert.Equal(0, store.Insertions);
    }

    [Fact]
    public async Task Event_five_persists_exact_callback_weights_and_one_Sap_work()
    {
        var store = new RecordingEventStore();
        var response = await PostAsync("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":5,"PesoBruto":"15000.00","Tara":"2654.33","PesoLiquido":"12345.67"}""", store);
        Assert.Equal(200, response.Status);
        Assert.Equal("OK", response.Body);
        Assert.Equal(1, store.Insertions);
        Assert.Equal(1, store.SapWork);
        var accepted = Assert.IsType<EventoGuardianRecebimento>(store.LastEvent);
        Assert.Equal(15000.00m, accepted.PesoBruto);
        Assert.Equal(2654.33m, accepted.Tara);
        Assert.Equal(12345.67m, accepted.PesoLiquido);
    }

    [Theory]
    [InlineData("PesoBruto")]
    [InlineData("Tara")]
    [InlineData("PesoLiquido")]
    public async Task Event_five_requires_each_weight_as_wire_string(string missing)
    {
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["ID"] = 98765, ["Sequencial"] = 12345, ["EstadoCodigo"] = 5,
            ["PesoBruto"] = "15000.00", ["Tara"] = "2654.33", ["PesoLiquido"] = "12345.67"
        };
        json.Remove(missing);
        var store = new RecordingEventStore();
        var response = await PostAsync(json.ToJsonString(), store);
        Assert.Equal(400, response.Status);
        Assert.Equal(0, store.Insertions);
        Assert.Equal(0, store.SapWork);
    }

    [Theory]
    [InlineData("PesoBruto", "15000.00")]
    [InlineData("Tara", "2654.33")]
    [InlineData("PesoLiquido", "12345.67")]
    public async Task Event_five_rejects_numeric_wire_weights(string field, string value)
    {
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["ID"] = 98765, ["Sequencial"] = 12345, ["EstadoCodigo"] = 5,
            ["PesoBruto"] = "15000.00", ["Tara"] = "2654.33", ["PesoLiquido"] = "12345.67"
        };
        json[field] = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var store = new RecordingEventStore();
        Assert.Equal(400, (await PostAsync(json.ToJsonString(), store)).Status);
        Assert.Equal(0, store.Insertions);
        Assert.Equal(0, store.SapWork);
    }

    [Theory]
    [InlineData("PesoBruto", "1,5")]
    [InlineData("Tara", "-1")]
    [InlineData("PesoLiquido", "1.0")]
    [InlineData("Tara", "0.12345678901234567890123456789")]
    public async Task Event_five_rejects_invalid_or_inexact_weights(string field, string value)
    {
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["ID"] = 98765, ["Sequencial"] = 12345, ["EstadoCodigo"] = 5,
            ["PesoBruto"] = "15000.00", ["Tara"] = "2654.33", ["PesoLiquido"] = "12345.67"
        };
        json[field] = value;
        var store = new RecordingEventStore();
        Assert.Equal(400, (await PostAsync(json.ToJsonString(), store)).Status);
        Assert.Equal(0, store.Insertions);
    }

    [Fact]
    public async Task Event_five_replay_is_idempotent_and_changed_tare_conflicts_without_new_work()
    {
        const string first = """{"ID":98765,"Sequencial":12345,"EstadoCodigo":5,"PesoBruto":"15000.00","Tara":"2654.33","PesoLiquido":"12345.67"}""";
        const string changed = """{"ID":98765,"Sequencial":12345,"EstadoCodigo":5,"PesoBruto":"15000.00","Tara":"2654.34","PesoLiquido":"12345.67"}""";
        var store = new RecordingEventStore();
        Assert.Equal(200, (await PostAsync(first, store)).Status);
        Assert.Equal(200, (await PostAsync(first, store)).Status);
        Assert.Equal(409, (await PostAsync(changed, store)).Status);
        Assert.Equal(1, store.Insertions);
        Assert.Equal(1, store.SapWork);
    }

    [Fact]
    public void Postgres_repository_has_atomic_event_five_projection_work_and_link()
    {
        var type = typeof(RepositorioEventoGuardianPostgreSql);
        string Sql(string field) => (string)type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Assert.Contains("callback_fingerprint = @callback_fingerprint", Sql("ProjectWeighingSql"));
        Assert.Contains("peso_bruto = @peso_bruto", Sql("ProjectWeighingSql"));
        Assert.Contains("tara_final = @tara_final", Sql("ProjectWeighingSql"));
        Assert.Contains("peso_liquido = @peso_liquido", Sql("ProjectWeighingSql"));
        Assert.DoesNotContain("update fugates.correlacao_guardian_evento", File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "FugaTES.Persistencia", "Recebimento", "RepositorioEventoGuardianPostgreSql.cs")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Event_five_preallocates_one_integration_id_and_rolls_back_if_work_insert_fails()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "FugaTES.Persistencia",
            "Recebimento", "RepositorioEventoGuardianPostgreSql.cs"));
        var lookup = source.IndexOf("if (existing is not null)", StringComparison.Ordinal);
        var allocation = source.IndexOf("var integrationId = evento.EstadoCodigo == 5 ? Guid.NewGuid() : (Guid?)null;", lookup, StringComparison.Ordinal);
        var ledgerParameter = source.IndexOf("insert.Parameters.AddWithValue(\"integracao_id\",", allocation, StringComparison.Ordinal);
        var ledgerInsert = source.IndexOf("insert.ExecuteNonQueryAsync(cancellationToken)", ledgerParameter, StringComparison.Ordinal);
        var projection = source.IndexOf("ProjectWeighingSql, connection, transaction", ledgerInsert, StringComparison.Ordinal);
        var workInsert = source.IndexOf("RegistrarCandidatoNaTransacaoAsync(", projection, StringComparison.Ordinal);
        var commit = source.IndexOf("await transaction.CommitAsync(cancellationToken);", workInsert, StringComparison.Ordinal);
        Assert.True(lookup >= 0 && allocation > lookup && ledgerParameter > allocation &&
            ledgerInsert > ledgerParameter && projection > ledgerInsert && workInsert > projection && commit > workInsert);
        Assert.Contains("insert.Parameters.AddWithValue(\"integracao_id\", NpgsqlDbType.Uuid,", source);
        Assert.Contains("integrationId.HasValue ? integrationId.Value : DBNull.Value", source);
        Assert.Contains("connection, transaction, candidate, integrationId!.Value, cancellationToken", source);
        Assert.Contains("if (registered != ResultadoRegistroIntegracao.Criada)", source);
        Assert.Contains("await transaction.RollbackAsync(CancellationToken.None);", source);
    }

    [Fact]
    public void Ledger_runtime_contract_needs_only_select_insert_and_nonweight_has_null_integration()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "FugaTES.Persistencia",
            "Recebimento", "RepositorioEventoGuardianPostgreSql.cs"));
        Assert.Contains("select callback_fingerprint, integracao_id from fugates.correlacao_guardian_evento", source);
        Assert.Contains("insert into fugates.correlacao_guardian_evento", source);
        Assert.DoesNotContain("update fugates.correlacao_guardian_evento", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evento.EstadoCodigo == 5 ? Guid.NewGuid() : (Guid?)null", source);
        Assert.Contains("integrationId.HasValue ? integrationId.Value : DBNull.Value", source);
    }

    [Fact]
    public void Productive_Postgres_route_uses_new_event_Dto_and_commits_after_work_link()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "FugaTES.Callback.Http", "Program.cs"));
        var endpoint = File.ReadAllText(Path.Combine(root, "src", "FugaTES.Callback.Http", "GuardianCallbackEndpoint.cs"));
        var repository = File.ReadAllText(Path.Combine(root, "src", "FugaTES.Persistencia", "Recebimento", "RepositorioEventoGuardianPostgreSql.cs"));
        Assert.Contains("GuardianCallbackEndpoint.MapDual(app, callback.Route)", program);
        Assert.Contains("ReadFromJsonAsync<GuardianEventoHttpDto>", endpoint);
        var register = repository.IndexOf("RegistrarCandidatoNaTransacaoAsync(", StringComparison.Ordinal);
        var commit = repository.IndexOf("await transaction.CommitAsync(cancellationToken);", register, StringComparison.Ordinal);
        Assert.True(register >= 0 && commit > register);
    }

    [Fact]
    public async Task Event_five_weights_reach_real_PesagemSapMvp_without_transformation()
    {
        const decimal gross = 12345.67m, tare = 2345.12m, net = 10000.55m;
        var store = new RecordingEventStore();
        var response = await PostAsync("""{"ID":98765,"Sequencial":12345,"EstadoCodigo":5,"PesoBruto":"12345.67","Tara":"2345.12","PesoLiquido":"10000.55"}""", store);
        Assert.Equal(200, response.Status);
        var evento = Assert.IsType<EventoGuardianRecebimento>(store.LastEvent);
        var fingerprint = Assert.IsType<string>(store.LastFingerprint);
        Assert.Equal(new PesosCallbackSapMvp(gross, tare, net),
            new PesosCallbackSapMvp(evento.PesoBruto!.Value, evento.Tara!.Value, evento.PesoLiquido!.Value));

        var input = new RecebimentoSapCpiMvp("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-R3",
            "BP-EMISSOR", "BP-TRANSPORTADORA", "CONTRATO-R3", "DOC-R3", "COMPRA",
            "CENTRO-R3", "ENTRADA", "NF", "PO-R3", 7, "ACM-R3");
        var projection = new CorrelacaoRecebimentoMvp("DOC-R3", "98765", input,
            TicketGuardian: "12345", CallbackFingerprint: fingerprint,
            Pesos: new PesosCallbackSapMvp(evento.PesoBruto.Value, evento.Tara.Value, evento.PesoLiquido.Value));
        var sap = new PesagemSapMvpGatewayFake();
        var processor = new ProcessadorPesagemSapMotor(new SnapshotRepository(projection), sap);
        var claim = new ClaimIntegracaoMotor(Guid.NewGuid(), Guid.NewGuid(), "DOC-R3", "GUARDIAN", "SAP",
            GuardianPesagemSapMotorWork.Operacao, "98765",
            GuardianPesagemSapMotorWork.Fingerprint("98765", fingerprint), 1, "R3-FOCAL",
            DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Null((await processor.ProcessarAsync(claim, CancellationToken.None)).Falha);
        var sent = Assert.Single(sap.Recebidas);
        Assert.Equal(gross, sent.PesoBruto);
        Assert.Equal(tare, sent.PesoTara);
        Assert.Equal(net, sent.PesoAntesDesconto);
    }

    [Fact]
    public void Program_dispatches_by_wire_while_preserving_both_legacy_handlers()
    {
        var root=FindRepositoryRoot();
        var program=File.ReadAllText(Path.Combine(root,"src","FugaTES.Callback.Http","Program.cs"));
        var endpoint=File.ReadAllText(Path.Combine(root,"src","FugaTES.Callback.Http","GuardianCallbackEndpoint.cs"));
        var dispatch=File.ReadAllText(Path.Combine(root,"src","FugaTES.Callback.Http","GuardianCallbackEndpoint.DualFormat.cs"));
        Assert.Contains("GuardianCallbackEndpoint.MapDual(app, callback.Route)",program);
        Assert.DoesNotContain("callback.CorrelationProvider",program);
        Assert.Contains("if(guardianEvent)await HandleEventoLegacyAsync(context);else await HandleLegacyAsync(context);",dispatch);
        var newHandler=endpoint[endpoint.IndexOf("private static async Task HandleEventoLegacyAsync",StringComparison.Ordinal)..endpoint.IndexOf("private static async Task HandleLegacyAsync",StringComparison.Ordinal)];
        var oldHandler=endpoint[endpoint.IndexOf("private static async Task HandleLegacyAsync",StringComparison.Ordinal)..];
        Assert.Contains("ReadFromJsonAsync<GuardianEventoHttpDto>",newHandler);
        Assert.DoesNotContain("ReadFromJsonAsync<GuardianCallbackHttpDto>",newHandler);
        Assert.Contains("ReadFromJsonAsync<GuardianCallbackHttpDto>",oldHandler);
        Assert.DoesNotContain("ReadFromJsonAsync<GuardianEventoHttpDto>",oldHandler);
    }

    private sealed class SnapshotRepository(CorrelacaoRecebimentoMvp snapshot) : IRepositorioCorrelacaoRecebimento
    {
        public Task<CorrelacaoRecebimentoMvp?> ObterPorProtocoloAsync(string protocolo, CancellationToken token) =>
            Task.FromResult<CorrelacaoRecebimentoMvp?>(protocolo == snapshot.ProtocoloGuardian ? snapshot : null);
        public Task<CorrelacaoRecebimentoMvp?> ObterPorDocumentoAsync(string documento, CancellationToken token) =>
            Task.FromResult<CorrelacaoRecebimentoMvp?>(documento == snapshot.DocumentoSap ? snapshot : null);
        public Task<ResultadoRegistroSnapshotMvp> RegistrarSnapshotAsync(RecebimentoSapCpiMvp entrada, CancellationToken token) => throw new NotSupportedException();
        public Task<ResultadoAssociacaoContratoAcmMvp> AssociarContratoAcmAsync(string documento, string acm, CancellationToken token) => throw new NotSupportedException();
        public Task<ResultadoAssociacaoProtocoloMvp> AssociarProtocoloAsync(string documento, string protocolo, CancellationToken token, DateTimeOffset criadoEm) => throw new NotSupportedException();
        public Task<ResultadoRegistroCallbackMvp> RegistrarCallbackAsync(CallbackGuardianMvp callback, CancellationToken token) => throw new NotSupportedException();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FugaTES.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static async Task<(int Status, string Body, string? ContentType)> PostAsync(string json, RecordingEventStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRegistroEventoGuardianStore>(store);
        services.AddSingleton<ProcessarEventoGuardianCasoDeUso>();
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Response.Body = new MemoryStream();
        await GuardianCallbackEndpoint.HandleEventoAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync(), context.Response.ContentType);
    }

    private sealed class RecordingEventStore : IRegistroEventoGuardianStore
    {
        private readonly Dictionary<(long, int), string> fingerprints = [];
        public string? LookupProtocol { get; private set; }
        public string? AttachedTicket { get; private set; }
        public int Insertions { get; private set; }
        public int SapWork { get; private set; }
        public EventoGuardianRecebimento? LastEvent { get; private set; }
        public string? LastFingerprint { get; private set; }

        public Task<StatusRegistroEventoGuardian> RegistrarAsync(EventoGuardianRecebimento evento, string fingerprint, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LookupProtocol = evento.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var ticket = evento.Sequencial.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (AttachedTicket is not null && AttachedTicket != ticket)
                return Task.FromResult(StatusRegistroEventoGuardian.Conflito);
            var key = (evento.Sequencial, evento.EstadoCodigo);
            if (fingerprints.TryGetValue(key, out var existing))
                return Task.FromResult(existing == fingerprint ? StatusRegistroEventoGuardian.Idempotente : StatusRegistroEventoGuardian.Conflito);
            AttachedTicket = ticket;
            fingerprints.Add(key, fingerprint);
            Insertions++;
            LastEvent = evento;
            LastFingerprint = fingerprint;
            if (evento.EstadoCodigo == 5) SapWork++;
            return Task.FromResult(StatusRegistroEventoGuardian.Novo);
        }
    }
}
