using FugaTES.Aplicacao.Configuracao;
using System.Globalization;
using System.Text.Json;
using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;

namespace FugaTES.WinForms.Monitor;

internal sealed class MonitorReadbackRepository : IMonitorReadRepository
{
    private readonly AcmMonitorProfile profile;
    private DateTimeOffset captured;
    private bool updatedAvailable;
    public string Estado => $"ACM 5433 - leitura exportada em {captured:yyyy-MM-dd HH:mm:ss 'UTC'}; historico nao exportado" +
        (updatedAvailable ? "" : "; ultima alteracao indisponivel");

    public MonitorReadbackRepository(AcmMonitorProfile profile)
    {
        this.profile = profile;
        if (!profile.IdentityValid || profile.Enabled || profile.ReadbackIntegrationId == Guid.Empty || string.IsNullOrWhiteSpace(profile.ReadbackContract) ||
            profile.ReadbackMaxAgeSeconds is < 1 or > 86400 || !Path.IsPathFullyQualified(profile.ReadbackFile) ||
            Path.GetFileName(profile.ReadbackFile) != "ACM_MONITOR_READBACK.json")
            throw new InvalidOperationException("ACM_READBACK_CONFIGURATION_REQUIRED");
        _ = Read();
    }

    private static string? Text(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => throw new InvalidOperationException("ACM_READBACK_SCALAR_REQUIRED") };
    }
    private static DateTimeOffset Date(JsonElement node, string name)
        => DateTimeOffset.Parse(Text(node, name)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

    private (MonitorIntegracao Row, MonitorAcmDetalhe Detail) Read()
    {
        try
        {
            var file = new FileInfo(profile.ReadbackFile);
            if (!file.Exists || file.Length is < 2 or > 262144 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException();
            using var stream = File.OpenRead(file.FullName);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var identity = root.GetProperty("identity");
            var integration = root.GetProperty("integration");
            var h4 = root.GetProperty("h4");
            var projection = root.GetProperty("projection");
            var timestamp = Date(root, "captured_utc");
            var age = DateTimeOffset.UtcNow - timestamp;
            if (Text(root, "schema") != "R165Y_ACM_MONITOR_READBACK_V1" || Text(root, "status") != "READ_ONLY_DURABLE_STATE_VERIFIED" ||
                identity.GetProperty("port").GetInt32() != 5433 || Text(identity, "cluster") != "18/fugatesacmdev" ||
                AmbientesBanco.Por(Text(identity,"database"),Text(identity,"role"),profile.Ambiente!)!=profile.Ambiente || Text(identity,"role")!=profile.Ambiente!.PapelAcm || Text(identity, "read_only") != "on" ||
                root.GetProperty("integration_id").GetGuid() != profile.ReadbackIntegrationId ||
                integration.GetProperty("id").GetGuid() != profile.ReadbackIntegrationId || h4.GetProperty("integracao_id").GetGuid() != profile.ReadbackIntegrationId ||
                Text(integration, "operacao") != "SAP_CONTRATO_TOLEDO" || Text(integration, "chave_negocio") != profile.ReadbackContract ||
                Text(h4, "numero_contrato") != profile.ReadbackContract || h4.GetProperty("versao").GetInt64() <= 0 ||
                age > TimeSpan.FromSeconds(profile.ReadbackMaxAgeSeconds) || age < TimeSpan.FromSeconds(-30) || projection.GetArrayLength() > 1)
                throw new InvalidOperationException();
            var p = projection.GetArrayLength() == 1 ? projection[0] : JsonSerializer.SerializeToElement(new { });
            var row = new MonitorIntegracao
            {
                IntegracaoId = profile.ReadbackIntegrationId, Documento = Text(integration, "chave_negocio"),
                Operacao = Text(integration, "operacao")!, CorrelationId = Text(integration, "correlation_id")!, EstadoMotor = Text(integration, "estado")!,
                CriadoEm = Date(integration, "criado_em"),
                AtualizadoEm = Text(integration, "atualizado_em") is null ? default : Date(integration, "atualizado_em"),
                Tentativa = Guid.TryParse(Text(integration, "tentativa_atual_id"), out var attempt) ? attempt : null,
                Fluxo = Text(p, "fluxo"), StatusFuncional = Text(p, "status_funcional"), EtapaFuncional = Text(p, "etapa_funcional"),
                UltimoResultado = Text(p, "resultado_funcional"), UltimoErro = Text(p, "erro_codigo"),
                ProtocoloGuardian = Text(p, "protocolo_guardian"), NumeroTicketGuardian = Text(p, "numero_ticket_guardian")
            };
            var facts = new MonitorAcmDados
            {
                ContratosDisponiveis = true, Contrato = Text(h4, "numero_contrato"), Versao = h4.GetProperty("versao").GetInt64(),
                Centro = Text(h4, "centro"), CodigoGuardian = Text(h4, "guardian_codigo_confirmado"), Referencia = Text(h4, "guardian_numero_referencia"),
                Protocolo = Text(h4, "guardian_protocolo_id"), Sequencial = Text(h4, "guardian_callback_sequencial"),
                RecebidoEm = Text(h4, "criado_em") is null ? null : Date(h4, "criado_em")
            }.Sanitizar();
            captured = timestamp;
            updatedAvailable = Text(integration, "atualizado_em") is not null;
            return (row, new(new([], [], []), facts));
        }
        catch { throw new InvalidOperationException("ACM_READBACK_MISSING_EXPIRED_OR_IDENTITY_REJECTED"); }
    }

    public Task<MonitorSnapshot> ConsultarAsync(MonitorFiltro filter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row = Read().Row;
        bool Match(string? expected, string? actual) => string.IsNullOrWhiteSpace(expected) || (actual?.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
        var group = filter.Grupo switch
        {
            "TODOS" => true,
            "PENDENTES" => row.EstadoMotor is "PENDENTE" or "EM_PROCESSAMENTO" or "AGUARDANDO_REPROCESSAMENTO" or "AGUARDANDO_ACAO_MANUAL" or "RESULTADO_INDETERMINADO" || row.StatusFuncional == "AGUARDANDO_CALLBACK",
            "FALHAS" => row.EstadoMotor is "AGUARDANDO_REPROCESSAMENTO" or "FALHA_DEFINITIVA",
            "INDETERMINADOS" => row.EstadoMotor == "RESULTADO_INDETERMINADO",
            _ => false
        };
        var included = group && Match(filter.Operacao, row.Operacao) && Match(filter.Estado, row.EstadoMotor) &&
            Match(filter.CorrelationId, row.CorrelationId) && Match(filter.Documento, row.Documento) &&
            (filter.De is null || row.CriadoEm >= filter.De) && (filter.Ate is null || row.CriadoEm <= filter.Ate);
        return Task.FromResult(new MonitorSnapshot(true, false, included ? [row] : []));
    }
    public Task<MonitorAcmDetalhe> DetalharAcmAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id != profile.ReadbackIntegrationId) throw new InvalidOperationException("ACM_READBACK_INTEGRATION_OUT_OF_SCOPE");
        return Task.FromResult(Read().Detail);
    }
    public async Task<MonitorDetalhe> DetalharAsync(Guid id, CancellationToken ct) => (await DetalharAcmAsync(id, ct)).Detalhe;
}
