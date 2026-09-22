using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Integracao.Toledo.Dtos.PreCadastro;

namespace FugaTES.Integracao.Toledo.Mapeamentos;

public static class MapeadorPreCadastroToledo
{
    public static PreCadastroToledoRequestDto Mapear(PreCadastroToledo preCadastro)
    {
        ArgumentNullException.ThrowIfNull(preCadastro);

        var codigoContrato = preCadastro.TipoMovimento switch
        {
            TipoMovimentoPreCadastro.Carga => preCadastro.NumeroContratoBase + "C",
            TipoMovimentoPreCadastro.Descarga => preCadastro.NumeroContratoBase + "D",
            _ => throw new ArgumentOutOfRangeException(nameof(preCadastro), preCadastro.TipoMovimento, "Tipo de movimento de pre-cadastro nao reconhecido.")
        };

        return new PreCadastroToledoRequestDto(
            new ConfiguracaoPreCadastroToledoDto(UrlParaRetorno: null),
            new DadosProtocoloPreCadastroToledoDto(
                Id: null,
                PlacaVeiculo: null,
                PlacaCarreta: null,
                Identificador: null,
                CodigoFluxo: preCadastro.CodigoFluxo,
                CodigoItem: preCadastro.CodigoItem,
                CodigoEmissor: null,
                CodigoTransportadora: preCadastro.CodigoTransportadora,
                CodigoMotorista: null,
                CodigoContrato: codigoContrato,
                CodigoVeiculo: null,
                PrevisaoCargaContrato: null,
                IdAgenda: 1,
                DataAgendamento: preCadastro.DataAgendamento,
                NumeroReferencia: preCadastro.NumeroReferencia,
                CamposAdicionais: [new CampoAdicionalPreCadastroToledoDto(1, "Interno")]));
    }
}
