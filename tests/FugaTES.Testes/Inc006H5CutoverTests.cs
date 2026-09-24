using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Infraestrutura.MotorIntegracao;
using FugaTES.Infraestrutura.Recebimento;
using FugaTES.Integracao.Toledo.Set26;

namespace FugaTES.Testes;

public sealed class Inc006H5CutoverTests
{
    [Fact]
    public async Task Boundary_RegistersOneLogicalWorkWithoutGuardianPost()
    {
        var guardian = new PreCadastroGuardianSet26GatewayFake("12345");
        var correlations = new RepositorioCorrelacaoRecebimentoMemoria();
        var motor = new RepositorioMotorIntegracaoFake();
        var options = new GuardianSet26Options("/pre-cadastro", "https://fugates.invalid/callback");
        var sut = new ProcessarRecebimentoMvpCasoDeUso(
            new RegistrarPreCadastroGuardianMotorCasoDeUso(correlations, motor, options));
        var input = new RecebimentoSapCpiMvp("RECEBIMENTO", "FLX_RECEBIMENTO", "MAT-1", "BP-E", "BP-T",
            "CONTRATO-1", "DOC-001", "COMPRA", "3011", "ENTRADA", "NF", "PO-1", 1, null);

        var first = await sut.ExecutarAsync(input, CancellationToken.None);
        var repeat = await sut.ExecutarAsync(input, CancellationToken.None);

        Assert.Null(first.ProtocoloGuardian);
        Assert.Null(repeat.ProtocoloGuardian);
        Assert.Equal("DOC-001", first.DocumentoSap);
        Assert.Single(motor.Integracoes);
        Assert.Equal(0, guardian.QuantidadeChamadas);
    }
}
