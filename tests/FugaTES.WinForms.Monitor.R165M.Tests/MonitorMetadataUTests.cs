using FugaTES.Aplicacao.Consultas.Monitor;
using FugaTES.Persistencia.Consultas.Monitor;
using FugaTES.WinForms.Monitor;

namespace FugaTES.Testes;

public sealed class MonitorMetadataUTests
{
    [Fact]
    public void Real_REST_rejected_stage_shows_persisted_error_not_protocol_completion()
    {
        var row = new MonitorIntegracao { EstadoMotor = "CONCLUIDA", StatusFuncional = "FALHA_NAO_REPROCESSAVEL", EtapaFuncional = "R165Y_REST_REJECTED", UltimoErro = "SYNTHETIC_HTTP_422" };
        var steps = MonitorFluxoAcm.Criar(row, new([], [], []), new() { CodigoGuardian = "001147", ContratosDisponiveis = true });
        var protocol = steps.Single(x => x.Etapa == "Protocolo Guardian");
        Assert.Equal("ERRO", protocol.Estado); Assert.Equal("SYNTHETIC_HTTP_422", protocol.Evidencia);
        Assert.Equal("ERRO", steps.Last().Estado);
    }

    private static readonly string ContractHash = new('A', 64);
    private static readonly string ProtocolHash = new('B', 64);
    private static MonitorIntegracao Row(string stage, string state, string metadata, string? protocol = null) => new()
    {
        Operacao = "SAP_CONTRATO_TOLEDO", Fluxo = "ACM_IN", UltimoResultado = metadata, EtapaFuncional = stage,
        StatusFuncional = state, ProtocoloGuardian = protocol
    };

    private static MonitorAcmDados Apply(MonitorAcmDados facts, MonitorIntegracao row) => facts.ComMetadataU(row);
    private static string? Sequence(MonitorAcmDados value) => value.SequencialContrato;

    [Theory]
    [InlineData("R165U_SOAP_CONFIRMED", "EM_PROCESSAMENTO", false)]
    [InlineData("R165U_PROTOCOL_BLOCKED", "AGUARDANDO_DEPENDENCIA_EXTERNA", false)]
    [InlineData("R165U_REST_ARMED", "RESULTADO_INDETERMINADO", true)]
    [InlineData("R165U_CONFIRMED", "AGUARDANDO_CALLBACK", true)]
    [InlineData("R165Y_REST_REJECTED", "FALHA_NAO_REPROCESSAVEL", true)]
    public void Confirmed_contract_sequence_is_read_from_U_not_callback(string stage, string state, bool protocolHash)
    {
        var row = Row(stage, state, $"U:{ContractHash}:{(protocolHash ? ProtocolHash : "")}:582", stage == "R165U_CONFIRMED" ? "000300" : null);
        var facts = new MonitorAcmDados { CodigoGuardian = "001147", Protocolo = row.ProtocoloGuardian, Sequencial = "00029" };
        var result = Apply(facts, row);
        Assert.Equal("582", Sequence(result));
        Assert.Equal("001147", result.CodigoGuardian); Assert.Equal("00029", result.Sequencial);
        Assert.NotEqual(Sequence(result), result.Sequencial);
        Assert.Equal(ContractHash, result.HashContratoMetadata);
        Assert.Equal(protocolHash ? ProtocolHash : null, result.HashProtocoloMetadata);
        Assert.Equal("FORMATO_E_ESTADO_VALIDOS", result.EstadoMetadataU);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+582")]
    [InlineData(" 582")]
    [InlineData("9223372036854775808")]
    [InlineData("582:extra")]
    public void Malformed_sequence_is_not_published(string sequence)
        => Assert.Null(Sequence(Apply(new() { CodigoGuardian = "001147" },
            Row("R165U_SOAP_CONFIRMED", "EM_PROCESSAMENTO", $"U:{ContractHash}::{sequence}"))));

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("ABC")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void Contract_digest_requires_exact_producer_uppercase_SHA256_format(string hash)
        => Assert.Null(Sequence(Apply(new() { CodigoGuardian = "001147" },
            Row("R165U_SOAP_CONFIRMED", "EM_PROCESSAMENTO", $"U:{hash}::582"))));

    [Theory]
    [InlineData("R165U_SOAP_ARMED", "RESULTADO_INDETERMINADO")]
    [InlineData("R165U_SOAP_CONFIRMED", "AGUARDANDO_CALLBACK")]
    [InlineData("R165U_RECONCILIATION_REQUIRED", "RESULTADO_INDETERMINADO")]
    public void Unconfirmed_or_incompatible_stage_cannot_publish_sequence(string stage, string state)
        => Assert.Null(Sequence(Apply(new() { CodigoGuardian = "001147" }, Row(stage, state, $"U:{ContractHash}::582"))));

    [Fact]
    public void Missing_H4_code_or_conflicting_protocol_blocks_sequence()
    {
        Assert.Null(Sequence(Apply(new(), Row("R165U_SOAP_CONFIRMED", "EM_PROCESSAMENTO", $"U:{ContractHash}::582"))));
        Assert.Null(Sequence(Apply(new() { CodigoGuardian = "001147", Protocolo = "301" },
            Row("R165U_CONFIRMED", "AGUARDANDO_CALLBACK", $"U:{ContractHash}:{ProtocolHash}:582", "300"))));
    }

    [Fact]
    public void Empty_sequence_is_unknown_not_callback_and_extra_protocol_hash_is_rejected()
    {
        Assert.Null(Sequence(Apply(new() { CodigoGuardian = "001147", Sequencial = "00029" },
            Row("R165U_SOAP_CONFIRMED", "EM_PROCESSAMENTO", $"U:{ContractHash}::"))));
        Assert.Null(Sequence(Apply(new() { CodigoGuardian = "001147" },
            Row("R165U_SOAP_CONFIRMED", "EM_PROCESSAMENTO", $"U:{ContractHash}:{ProtocolHash}:582"))));
    }

    [Fact]
    public void Projection_columns_are_the_actual_U_status_and_stage_sources()
    {
        var sql = MonitorPostgreSqlReadRepository.ConstruirSql(true, false, false);
        Assert.Contains("projecao_resultado_fluxo_mvp", sql);
        Assert.Contains("coalesce(p.resultado_funcional,t.desfecho) as \"UltimoResultado\"", sql);
        Assert.Contains("p.status_funcional as \"StatusFuncional\"", sql);
        Assert.Contains("p.etapa_funcional as \"EtapaFuncional\"", sql);
        Assert.Contains("p.erro_codigo as \"UltimoErro\"", sql);
    }

    [Fact]
    public void Wrong_operation_flow_ambiguous_H4_or_invalid_protocol_digest_never_publish_sequence()
    {
        var facts = new MonitorAcmDados { CodigoGuardian = "001147" };
        var row = Row("R165U_REST_ARMED", "RESULTADO_INDETERMINADO", $"U:{ContractHash}:{ProtocolHash}:582");
        Assert.Null(Sequence(Apply(facts, row with { Operacao = "SAP_RECEBIMENTO_GUARDIAN_PESAGEM" })));
        Assert.Null(Sequence(Apply(facts, row with { Fluxo = "MM_IN" })));
        Assert.Null(Sequence(Apply(facts with { CorrelacaoAmbigua = true }, row)));
        Assert.Null(Sequence(Apply(facts, row with { UltimoResultado = $"U:{ContractHash}:ABC:582" })));
    }

    [Fact]
    public void Valid_literal_sequence_is_not_normalized_and_no_digest_is_claimed_to_match_H4_fingerprint()
    {
        var result = Apply(new() { CodigoGuardian = "001147" }, Row("R165U_SOAP_CONFIRMED", "EM_PROCESSAMENTO", $"U:{ContractHash}::000582"));
        Assert.Equal("000582", Sequence(result));
        Assert.Equal("FORMATO_E_ESTADO_VALIDOS", result.EstadoMetadataU);
    }
}
