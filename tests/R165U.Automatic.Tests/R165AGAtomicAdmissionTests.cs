using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using Xunit;

public sealed class R165AGAtomicAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026,10,26,12,0,0,TimeSpan.Zero);
    private static AcmInboundAdmission Admission => new(Guid.Parse("11111111-2222-3333-4444-555555555555"),Now.AddMinutes(-5),Now.AddMinutes(20));
    private static string Correlation => Admission.CorrelationPrefix + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static VersaoContratoSapToledo Version(string number="0000999001") => new AutomaticFixture().Add(number).Version;
    private static AcmInboundAdmittedRow Row(VersaoContratoSapToledo v) => new(v,Correlation,Now,"SAP","TOLEDO","SAP_CONTRATO_TOLEDO",v.NumeroContrato,v.FingerprintSha256);

    [Fact]
    public void Empty_window_allows_atomic_first_registration_without_historical_ID()
    {
        var v=Version();
        Assert.Null(AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,v.FingerprintSha256,Correlation,Now,[]));
    }

    [Fact]
    public void Identical_admitted_redelivery_returns_original_identity_and_version()
    {
        var v=Version();
        var result=AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,v.FingerprintSha256,
            Admission.CorrelationPrefix+"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",Now,[Row(v)]);
        Assert.Equal(v.IntegracaoId,result!.IntegracaoId);
        Assert.Equal(1,result.Versao);
        Assert.Equal(ResultadoRegistroVersaoContrato.JaExistente,result.Resultado);
        Assert.Equal(Correlation,result.AdmissionCorrelationId);
    }

    [Fact]
    public void Second_contract_cannot_take_an_already_bound_window()
    {
        var first=Version();var second=Version("0000999002");
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,
            second.Snapshot,second.FingerprintSha256,Correlation,Now,[Row(first)]));
    }

    [Fact]
    public void Changed_payload_is_not_an_identical_redelivery()
    {
        var v=Version();var changed=v.Snapshot with { QuantidadeContrato=999m };
        var hash=new ContratoSapToledoCanonicalizer().Canonicalizar(changed).FingerprintSha256;
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,changed,hash,Correlation,Now,[Row(v)]));
    }

    [Theory]
    [InlineData(-6)] [InlineData(20)] [InlineData(21)]
    public void Outside_window_never_reserves(int minutes)
    {
        var v=Version();
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,v.FingerprintSha256,Correlation,Now.AddMinutes(minutes),[]));
    }

    [Fact]
    public void Lost_or_ambiguous_partial_registration_is_not_repaired()
    {
        var v=Version();
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,v.FingerprintSha256,Correlation,Now,[Row(v) with {Version=null}]));
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,v.FingerprintSha256,Correlation,Now,[Row(v),Row(v)]));
    }

    [Fact]
    public void Historical_marker_or_version_is_not_current_admission()
    {
        var v=Version();
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,v.FingerprintSha256,Correlation,Now,[Row(v) with {CorrelationId="OLD_REQUEST"}]));
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,v.FingerprintSha256,Correlation,Now,[Row(v with {Versao=2})]));
    }

    [Fact]
    public void Corrupt_fingerprint_and_original_business_identity_block()
    {
        var v=Version();
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,new string('A',64),Correlation,Now,[]));
        Assert.Throws<AcmInboundScopeConflictException>(()=>AcmInboundAdmissionGuard.Resolver(Admission,v.Snapshot,v.FingerprintSha256,Correlation,Now,[Row(v) with {Origem="MANUAL"}]));
    }
}
