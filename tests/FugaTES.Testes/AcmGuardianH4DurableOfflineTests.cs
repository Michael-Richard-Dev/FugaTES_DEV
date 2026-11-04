using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.Mvp;
using FugaTES.Aplicacao.Servicos.SAP;

namespace FugaTES.Testes;

public sealed class AcmGuardianH4DurableOfflineTests
{
    private static ContratoSapToledoSnapshot Snapshot(string number = "0000001031", string center = "3011")
        => new("Toledo", number, "AC3P", null, null, null, null, null, null, "T",
            null, null, null, null, null, null, null, null, null, null,
            center, "10", []);

    [Fact]
    public void OC1031_preserva_numero_contrato_SAP_sem_reconstrucao_por_padding()
    {
        var result = MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot());
        Assert.Equal("0000001031", result.NumeroContratoSap);
        Assert.Equal("OC1031", result.NumeroReferencia);
        Assert.Equal("308102", result.Estado);
        Assert.Equal("309837", result.TipoLancamento);
    }

    [Fact]
    public void Referencia_nao_e_identidade_SAP_e_nao_identifica_versao_H4()
    {
        var withPadding = MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot());
        var withoutPadding = MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot("1031"));
        Assert.Equal(withPadding.NumeroReferencia, withoutPadding.NumeroReferencia);
        Assert.NotEqual(withPadding.NumeroContratoSap, withoutPadding.NumeroContratoSap);
        var v1 = new AcmGuardianVinculo("0000001031", 1, Guid.NewGuid(), "OC1031", "C-1", null,
            null, null, null, null, null);
        var v2 = v1 with { VersaoH4 = 2, IntegracaoIdOrigem = Guid.NewGuid() };
        Assert.NotEqual(v1.VersaoH4, v2.VersaoH4);
        Assert.NotEqual(v1.IntegracaoIdOrigem, v2.IntegracaoIdOrigem);
    }

    [Fact]
    public void Identidade_H4_e_callback_distintas_sobrevivem_roundtrip_de_modelo()
    {
        var original = new AcmGuardianVinculo("0000001031", 2, Guid.NewGuid(), "OC1031",
            "0000001031", "0000001031", 71, "TICKET-71", 991, 812, new string('A', 64));
        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var recovered = System.Text.Json.JsonSerializer.Deserialize<AcmGuardianVinculo>(json);
        Assert.Equal(original, recovered);
        Assert.NotEqual(original.ProtocoloIdGuardian, original.CallbackIdGuardian);
        Assert.Equal("0000001031", recovered!.NumeroContratoSap);
        Assert.Equal(2, recovered.VersaoH4);
    }

    [Fact]
    public void Cenário_nao_aprovado_falha_fechado()
    {
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot(center: "3020")));
    }

    [Fact]
    public void Referencia_comprida_falha_fechado()
    {
        Assert.Throws<DecisaoFuncionalPendenteSapException>(() =>
            MapeadorContratoAcmGuardian.MapearCamposConfirmados(Snapshot("12345678901234567890")));
    }
}
