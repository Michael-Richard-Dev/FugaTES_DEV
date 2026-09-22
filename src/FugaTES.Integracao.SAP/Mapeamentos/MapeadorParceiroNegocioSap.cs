using FugaTES.Aplicacao.Modelos.SAP;
using FugaTES.Aplicacao.Resultados.SAP;
using FugaTES.Integracao.SAP.Dtos.BusinessPartner;

namespace FugaTES.Integracao.SAP.Mapeamentos;

internal sealed class MapeadorParceiroNegocioSap
{
    private readonly TimeProvider timeProvider;

    public MapeadorParceiroNegocioSap(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ResultadoConsultaSap<ParceiroNegocioConsultado> Mapear(BusinessPartnerSapDto dto, CategoriaParceiroNegocio categoria)
    {
        if (!ValidarContratoColecoes(dto) || TemPaginacaoParcial(dto))
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaContrato);
        }

        if (string.IsNullOrWhiteSpace(dto.BusinessPartner))
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(StatusConsultaSap.FalhaContrato);
        }

        var validacaoCategoria = ValidarCategoria(dto, categoria);
        if (validacaoCategoria != StatusConsultaSap.Sucesso)
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(validacaoCategoria);
        }

        var documentos = MapeadorDocumentoFiscalSap.Mapear(dto.ToBusinessPartnerTax!.Results!);
        if (documentos.Status != StatusConsultaSap.Sucesso)
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(documentos.Status);
        }

        var endereco = SelecionarEndereco(dto);
        if (endereco.Status != StatusConsultaSap.Sucesso)
        {
            return ResultadoConsultaSap<ParceiroNegocioConsultado>.Falha(endereco.Status);
        }

        var parceiro = new ParceiroNegocioConsultado(
            dto.BusinessPartner,
            ObterNome(dto),
            documentos.Documentos,
            endereco.Endereco,
            ObterTelefones(dto),
            ObterEmails(dto),
            ObterBloqueio(dto.BusinessPartnerIsBlocked),
            categoria)
        {
            BusinessPartnerCategory = dto.BusinessPartnerCategory,
            NomeCompletoParceiroNegocio = dto.BusinessPartnerFullName
        };

        return ResultadoConsultaSap<ParceiroNegocioConsultado>.Sucesso(parceiro);
    }

    private StatusConsultaSap ValidarCategoria(BusinessPartnerSapDto dto, CategoriaParceiroNegocio categoria)
    {
        return categoria switch
        {
            CategoriaParceiroNegocio.Cliente => ValidarNavegacaoCliente(dto),
            CategoriaParceiroNegocio.Fornecedor => ValidarNavegacaoFornecedor(dto),
            CategoriaParceiroNegocio.Transportadora => ValidarTransportadora(dto),
            _ => StatusConsultaSap.FalhaContrato
        };
    }

    private static StatusConsultaSap ValidarNavegacaoCliente(BusinessPartnerSapDto dto)
    {
        if (dto.ToCustomer is null)
        {
            return StatusConsultaSap.CategoriaNaoCorrespondente;
        }

        return dto.ToCustomer.Results is null ? StatusConsultaSap.Sucesso : StatusConsultaSap.FalhaContrato;
    }

    private static StatusConsultaSap ValidarNavegacaoFornecedor(BusinessPartnerSapDto dto)
    {
        if (dto.ToSupplier is null)
        {
            return StatusConsultaSap.CategoriaNaoCorrespondente;
        }

        return dto.ToSupplier.Results is null ? StatusConsultaSap.Sucesso : StatusConsultaSap.FalhaContrato;
    }

    private StatusConsultaSap ValidarTransportadora(BusinessPartnerSapDto dto)
    {
        var roles = dto.ToBusinessPartnerRole!.Results!.Where(role => role.BusinessPartnerRole == "CRM010").ToArray();
        if (roles.Length == 0)
        {
            return StatusConsultaSap.CategoriaNaoCorrespondente;
        }

        var referencia = timeProvider.GetUtcNow();
        var existeVigente = false;

        foreach (var role in roles)
        {
            if (!ParserDataODataV2.TentarParse(role.ValidFrom, out var inicio) || !ParserDataODataV2.TentarParse(role.ValidTo, out var fim))
            {
                return StatusConsultaSap.FalhaContrato;
            }

            if (inicio <= referencia && referencia <= fim)
            {
                existeVigente = true;
            }
        }

        return existeVigente ? StatusConsultaSap.Sucesso : StatusConsultaSap.CategoriaNaoCorrespondente;
    }

    private ResultadoEndereco SelecionarEndereco(BusinessPartnerSapDto dto)
    {
        var referencia = timeProvider.GetUtcNow();
        var candidatos = new List<EnderecoBusinessPartnerSapDto>();

        foreach (var endereco in dto.ToBusinessPartnerAddress!.Results!)
        {
            foreach (var uso in endereco.ToAddressUsage!.Results!.Where(uso => uso.AddressUsage == "XXDEFAULT"))
            {
                if (!ParserDataODataV2.TentarParse(uso.ValidityStartDate, out var inicio) || !ParserDataODataV2.TentarParse(uso.ValidityEndDate, out var fim))
                {
                    return ResultadoEndereco.Falha(StatusConsultaSap.FalhaContrato);
                }

                if (inicio <= referencia && referencia <= fim)
                {
                    candidatos.Add(endereco);
                }
            }
        }

        if (candidatos.Count == 0)
        {
            return ResultadoEndereco.Sucesso(null);
        }

        if (candidatos.Count > 1)
        {
            return ResultadoEndereco.Falha(StatusConsultaSap.FalhaContrato);
        }

        var selecionado = candidatos[0];
        return ResultadoEndereco.Sucesso(new EnderecoParceiroNegocio(
            selecionado.AddressID ?? string.Empty,
            selecionado.StreetName,
            selecionado.CityName,
            selecionado.Region,
            selecionado.PostalCode,
            selecionado.Country));
    }

    private static bool ValidarContratoColecoes(BusinessPartnerSapDto dto)
    {
        if (!TemResultsSemNulos(dto.ToBusinessPartnerRole) || !TemResultsSemNulos(dto.ToBusinessPartnerTax) || !TemResultsSemNulos(dto.ToBusinessPartnerAddress))
        {
            return false;
        }

        foreach (var endereco in dto.ToBusinessPartnerAddress!.Results!)
        {
            if (!TemResultsSemNulos(endereco.ToAddressUsage) || !TemResultsSemNulos(endereco.ToPhoneNumber) || !TemResultsSemNulos(endereco.ToEmailAddress))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TemResultsSemNulos<T>(ODataResultsDto<T>? colecao)
    {
        if (colecao?.Results is null)
        {
            return false;
        }

        foreach (var item in colecao.Results)
        {
            if (item is null)
            {
                return false;
            }
        }

        return true;
    }
    private static bool TemPaginacaoParcial(BusinessPartnerSapDto dto)
    {
        if (TemNext(dto.ToBusinessPartnerRole) || TemNext(dto.ToBusinessPartnerTax) || TemNext(dto.ToBusinessPartnerAddress))
        {
            return true;
        }

        foreach (var endereco in dto.ToBusinessPartnerAddress!.Results!)
        {
            if (TemNext(endereco.ToAddressUsage) || TemNext(endereco.ToPhoneNumber) || TemNext(endereco.ToEmailAddress))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TemNext<T>(ODataResultsDto<T>? colecao)
    {
        return !string.IsNullOrWhiteSpace(colecao?.Next);
    }


    private static string? ObterNome(BusinessPartnerSapDto dto)
    {
        var nome = PrimeiroPreenchido(dto.BusinessPartnerFullName, dto.OrganizationBPName1);
        if (nome is not null)
        {
            return nome;
        }

        var nomeCompleto = string.Join(' ', new[] { dto.FirstName, dto.LastName }.Where(valor => !string.IsNullOrWhiteSpace(valor)));
        return string.IsNullOrWhiteSpace(nomeCompleto) ? null : nomeCompleto;
    }

    private static string? PrimeiroPreenchido(params string?[] valores)
    {
        return valores.FirstOrDefault(valor => !string.IsNullOrWhiteSpace(valor));
    }

    private static IReadOnlyCollection<string> ObterTelefones(BusinessPartnerSapDto dto)
    {
        return dto.ToBusinessPartnerAddress!.Results!
            .SelectMany(endereco => endereco.ToPhoneNumber!.Results!)
            .Select(telefone => telefone.PhoneNumber)
            .Where(valor => !string.IsNullOrWhiteSpace(valor))
            .Select(valor => valor!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<string> ObterEmails(BusinessPartnerSapDto dto)
    {
        return dto.ToBusinessPartnerAddress!.Results!
            .SelectMany(endereco => endereco.ToEmailAddress!.Results!)
            .Select(email => email.EmailAddress)
            .Where(valor => !string.IsNullOrWhiteSpace(valor))
            .Select(valor => valor!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static SituacaoBloqueioSap ObterBloqueio(string? bloqueio)
    {
        if (string.Equals(bloqueio, "false", StringComparison.OrdinalIgnoreCase))
        {
            return SituacaoBloqueioSap.NaoBloqueado;
        }

        if (string.IsNullOrWhiteSpace(bloqueio))
        {
            return SituacaoBloqueioSap.Indeterminado;
        }

        if (string.Equals(bloqueio, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(bloqueio, "X", StringComparison.OrdinalIgnoreCase))
        {
            return SituacaoBloqueioSap.Bloqueado;
        }

        return SituacaoBloqueioSap.Indeterminado;
    }

    private sealed class ResultadoEndereco
    {
        private ResultadoEndereco(StatusConsultaSap status, EnderecoParceiroNegocio? endereco)
        {
            Status = status;
            Endereco = endereco;
        }

        public StatusConsultaSap Status { get; }

        public EnderecoParceiroNegocio? Endereco { get; }

        public static ResultadoEndereco Sucesso(EnderecoParceiroNegocio? endereco)
        {
            return new ResultadoEndereco(StatusConsultaSap.Sucesso, endereco);
        }

        public static ResultadoEndereco Falha(StatusConsultaSap status)
        {
            return new ResultadoEndereco(status, null);
        }
    }
}
