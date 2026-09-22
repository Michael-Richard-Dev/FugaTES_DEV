using System.Globalization;
using FugaTES.Aplicacao.CasosDeUso.Toledo;
using FugaTES.Aplicacao.Contratos.ContratosSapToledo;
using FugaTES.Aplicacao.Contratos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.MotorIntegracao;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.Toledo;
namespace FugaTES.Aplicacao.Servicos.ContratosSapToledo;
public sealed class ProcessadorContratoSapToledo(IContratoSapToledoVersaoStore store, EnviarContratoToledoCasoDeUso useCase) : IProcessadorExternoIntegracao
{
    public async Task<ResultadoProcessamentoExterno> ProcessarAsync(ClaimIntegracaoMotor claim,CancellationToken ct)
    {
        if (claim.Operacao!="SAP_CONTRATO_TOLEDO") return new(ClassificacaoFalhaIntegracao.FalhaDefinitiva);
        var version=await store.ObterPorIntegracaoIdAsync(claim.IntegracaoId,ct) ?? throw new InvalidOperationException("H4 version not found."); var s=version.Snapshot;
        var contract=new ContratoToledo(s.SistemaBalanca,s.NumeroContrato,s.TipoContrato,s.DescricaoTipoContrato,s.DataEmissao,s.DataEntregaInicio,s.DataEntregaFim,s.DiasToler?.ToString(CultureInfo.InvariantCulture),s.DataLimiteEntrega,s.StatusContrato,s.QuantidadeContrato?.ToString("G29",CultureInfo.InvariantCulture),s.QuantidadeSaldoDevolucao?.ToString("G29",CultureInfo.InvariantCulture),s.IdParceiro,s.Material,s.TipoPesagem,s.TabelaQualidadeID,s.TabelaQualidadeVersao,s.TabelaQualidadeVersaoValidade,s.Observacao,s.ValorUnitario?.ToString("G29",CultureInfo.InvariantCulture),s.Centro,s.OperacaoContrato,s.CentroOpcional.Select(x=>new CentroOpcionalContratoToledo(x)).ToArray());
        var result=await useCase.ExecutarAsync(contract,ct);
        return result.Status switch { StatusOperacaoToledo.Sucesso=>ResultadoProcessamentoExterno.Sucesso, StatusOperacaoToledo.FalhaContrato=>new(ClassificacaoFalhaIntegracao.FalhaDefinitiva), _=>new(ClassificacaoFalhaIntegracao.ResultadoIndeterminado)};
    }
}
