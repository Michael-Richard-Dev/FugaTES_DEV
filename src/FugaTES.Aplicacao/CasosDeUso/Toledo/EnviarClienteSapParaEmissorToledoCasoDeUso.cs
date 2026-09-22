using FugaTES.Aplicacao.Contratos.Gateways.SAP;
using FugaTES.Aplicacao.Contratos.Gateways.Toledo;
using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Modelos.Toledo;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Aplicacao.Resultados.Toledo;

namespace FugaTES.Aplicacao.CasosDeUso.Toledo;

public sealed class EnviarClienteSapParaEmissorToledoCasoDeUso
{
    private const int TipoEmissorCliente = 2;
    private const int CategoriaPessoaFisica = 1;
    private const int CategoriaPessoaJuridica = 2;
    private const int TipoDocumentoPessoaFisica = 303404;
    private const int TipoDocumentoPessoaJuridica = 303405;

    private static readonly IReadOnlyDictionary<string, int> UfsToledo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["SP"] = 26
    };

    private readonly IConsultaClienteSapGateway consultaClienteSapGateway;
    private readonly IEmissorToledoGateway emissorToledoGateway;

    public EnviarClienteSapParaEmissorToledoCasoDeUso(IConsultaClienteSapGateway consultaClienteSapGateway, IEmissorToledoGateway emissorToledoGateway)
    {
        this.consultaClienteSapGateway = consultaClienteSapGateway ?? throw new ArgumentNullException(nameof(consultaClienteSapGateway));
        this.emissorToledoGateway = emissorToledoGateway ?? throw new ArgumentNullException(nameof(emissorToledoGateway));
    }

    public async Task<ResultadoOperacaoToledo<EmissorToledoRegistrado>> ExecutarAsync(string businessPartner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(businessPartner);
        if (string.IsNullOrWhiteSpace(businessPartner))
        {
            throw new ArgumentException("BusinessPartner nao pode ser vazio.", nameof(businessPartner));
        }

        var consultaSap = await consultaClienteSapGateway.ConsultarAsync(businessPartner, cancellationToken).ConfigureAwait(false);
        if (consultaSap.Status != StatusConsultaSap.Sucesso || consultaSap.Dados is null || consultaSap.Dados.CategoriaValidada != CategoriaParceiroNegocio.Cliente)
        {
            return ResultadoOperacaoToledo<EmissorToledoRegistrado>.Falha(StatusOperacaoToledo.FalhaContrato);
        }

        var emissor = MapearEmissor(consultaSap.Dados);
        if (emissor is null)
        {
            return ResultadoOperacaoToledo<EmissorToledoRegistrado>.Falha(StatusOperacaoToledo.FalhaContrato);
        }

        return await emissorToledoGateway.EnviarAsync(emissor, cancellationToken).ConfigureAwait(false);
    }

    private static EmissorToledo? MapearEmissor(ParceiroNegocioConsultado cliente)
    {
        var tipoDocumento = MapearTipoDocumento(cliente.BusinessPartnerCategory);
        if (tipoDocumento is null || string.IsNullOrWhiteSpace(cliente.NomeCompletoParceiroNegocio))
        {
            return null;
        }

        var cnpj = ObterDocumento(cliente, TipoDocumentoFiscal.Cnpj);
        if (cliente.BusinessPartnerCategory == CategoriaPessoaJuridica && string.IsNullOrWhiteSpace(cnpj))
        {
            return null;
        }

        var inscricaoEstadual = ObterDocumento(cliente, TipoDocumentoFiscal.InscricaoEstadual);
        var email = cliente.Emails.FirstOrDefault(email => !string.IsNullOrWhiteSpace(email));
        var endereco = cliente.EnderecoSelecionado;
        string? municipio = null;
        int? uf = null;
        string? cep = null;

        if (endereco is not null)
        {
            if (string.IsNullOrWhiteSpace(endereco.Regiao) || !UfsToledo.TryGetValue(endereco.Regiao, out var ufToledo))
            {
                return null;
            }

            municipio = string.IsNullOrWhiteSpace(endereco.Cidade) ? null : endereco.Cidade;
            uf = ufToledo;
            cep = string.IsNullOrWhiteSpace(endereco.CodigoPostal) ? null : endereco.CodigoPostal;
        }

        return new EmissorToledo(
            cliente.BusinessPartner,
            cliente.NomeCompletoParceiroNegocio,
            TipoEmissorCliente,
            tipoDocumento.Value,
            cnpj,
            inscricaoEstadual,
            municipio,
            uf,
            cep,
            email);
    }

    private static int? MapearTipoDocumento(int? businessPartnerCategory)
    {
        return businessPartnerCategory switch
        {
            CategoriaPessoaFisica => TipoDocumentoPessoaFisica,
            CategoriaPessoaJuridica => TipoDocumentoPessoaJuridica,
            _ => null
        };
    }

    private static string? ObterDocumento(ParceiroNegocioConsultado cliente, TipoDocumentoFiscal tipo)
    {
        return cliente.DocumentosFiscais.FirstOrDefault(documento => documento.Tipo == tipo && !string.IsNullOrWhiteSpace(documento.Valor))?.Valor;
    }
}