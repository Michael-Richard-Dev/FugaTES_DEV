using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Servicos.MotorIntegracao;
using Xunit;

// R164F values supplied by the human for R165D. This does not select a scenario
// or attest any technical field. All ticket identities below remain synthetic.
public sealed class R165DApprovedOperationProvenanceTests
{
    [Theory]
    [InlineData(1, null)] [InlineData(10, null)]
    [InlineData(40, null)] [InlineData(50, null)]
    [InlineData(1, "OperacaoContrato")] [InlineData(10, "OperacaoContrato")]
    [InlineData(40, "OperacaoContrato")] [InlineData(50, "OperacaoContrato")]
    public async Task Approved_numeric_value_cannot_replace_a_proven_technical_source(short value, string? provenance)
    {
        var (correlation, claim, evidence) = EvidenceDurabilityTests.Fixture();
        evidence = evidence with {
            TipoOperacaoSap = value,
            TipoOperacaoSapProveniencia = provenance,
            TipoOperacaoSapEvidenceReference = null
        };
        var sap = new EvidenceDurabilityTests.Sap();
        var store = new EvidenceDurabilityTests.MemoryEvidenceStore();
        var result = await new ProcessadorPesagemSapMotor(
            new EvidenceDurabilityTests.Correlations(correlation), sap,
            preflight: new EvidenceDurabilityTests.Guard(),
            ticketEvidenceSource: new EvidenceDurabilityTests.Source(evidence),
            ticketEvidenceStore: store).ProcessarAsync(claim, default);

        Assert.Equal(ClassificacaoFalhaIntegracao.FalhaDefinitiva, result.Falha);
        Assert.Equal(0, sap.Calls);
        Assert.Null(store.Snapshot);
        Assert.False(store.Committed);
    }
}
