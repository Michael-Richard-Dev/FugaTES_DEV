using FugaTES.Aplicacao.Contratos.Guardian;
using FugaTES.Aplicacao.Modelos.Guardian;
using FugaTES.Aplicacao.Servicos.SAP;

namespace FugaTES.Aplicacao.Servicos.Mvp;

public enum AcaoSincronizacaoContrato { NoAction, Create, Update, Indeterminado }
public sealed record ResultadoSincronizacaoContrato(AcaoSincronizacaoContrato Acao, string? CodigoConfirmado = null);

public sealed class SincronizarContratoGuardian(IGuardianContratoSincronizacaoGateway gateway)
{
    public static bool Identicos(ContratoGuardianSet26 atual, ContratoGuardianSet26 desejado)
        => atual.Codigo == desejado.Codigo && atual.Descricao == desejado.Descricao &&
           atual.ValorContrato == desejado.ValorContrato && atual.Estado == desejado.Estado &&
           atual.ValorProximidadeFimContrato == desejado.ValorProximidadeFimContrato &&
           atual.TipoLancamento == desejado.TipoLancamento &&
           (desejado.CodigoEmissor is null || atual.CodigoEmissor == desejado.CodigoEmissor) &&
           (desejado.CodigoItem is null || atual.CodigoItem == desejado.CodigoItem) &&
           (desejado.Veiculos is null || (atual.Veiculos is not null &&
             atual.Veiculos.OrderBy(v => v.Codigo).ThenBy(v => v.PlacaVeiculo)
                .SequenceEqual(desejado.Veiculos.OrderBy(v => v.Codigo).ThenBy(v => v.PlacaVeiculo))));

    public async Task<ResultadoSincronizacaoContrato> ExecutarAsync(ContratoGuardianSet26 desejado,
        CancellationToken ct, Action? antesDeEscrita = null)
    {
        // Read failures propagate without marking any external mutation.
        var consulta = await gateway.ConsultarAsync(desejado.Codigo, ct);
        if (consulta.Estado == EstadoConsultaContratoGuardian.Indeterminado)
            return new(AcaoSincronizacaoContrato.Indeterminado);
        var atual = consulta.Contrato;
        if (consulta.Estado == EstadoConsultaContratoGuardian.Existente)
        {
            if (atual is null || atual.Codigo != desejado.Codigo)
                return new(AcaoSincronizacaoContrato.Indeterminado);
            if (atual.TipoLancamento != desejado.TipoLancamento)
                throw new DecisaoFuncionalPendenteSapException("ACM_TIPO_LANCAMENTO_IMMUTABLE");
            if (Identicos(atual, desejado)) return new(AcaoSincronizacaoContrato.NoAction, atual.Codigo);
        }
        else if (consulta.Estado != EstadoConsultaContratoGuardian.InexistenteComprovado || atual is not null)
            return new(AcaoSincronizacaoContrato.Indeterminado);
        ct.ThrowIfCancellationRequested();
        var acao = atual is null ? AcaoSincronizacaoContrato.Create : AcaoSincronizacaoContrato.Update;
        antesDeEscrita?.Invoke();
        try
        {
            var resposta = atual is null
                ? await gateway.CadastrarComRespostaAsync(desejado, ct)
                : await gateway.AlterarComRespostaAsync(atual, desejado, ct);
            return resposta.CodigoContratoConfirmado == desejado.Codigo
                ? new(acao, desejado.Codigo) : new(AcaoSincronizacaoContrato.Indeterminado);
        }
        catch (Exception)
        {
            // A possible mutation with unknown outcome must never cause an automatic retry.
            return new(AcaoSincronizacaoContrato.Indeterminado);
        }
    }
}