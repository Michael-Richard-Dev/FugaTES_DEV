using System.Text.Json.Serialization;

namespace FugaTES.Integracao.SAP.Dtos.BusinessPartner;

internal sealed class BusinessPartnerSapDto
{
    public string? BusinessPartner { get; set; }

    public string? BusinessPartnerFullName { get; set; }

    public int? BusinessPartnerCategory { get; set; }

    public string? OrganizationBPName1 { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? BusinessPartnerIsBlocked { get; set; }

    [JsonPropertyName("to_Customer")]
    public ClienteSapDto? ToCustomer { get; set; }

    [JsonPropertyName("to_Supplier")]
    public FornecedorSapDto? ToSupplier { get; set; }

    [JsonPropertyName("to_BusinessPartnerRole")]
    public ODataResultsDto<RoleBusinessPartnerSapDto>? ToBusinessPartnerRole { get; set; }

    [JsonPropertyName("to_BusinessPartnerTax")]
    public ODataResultsDto<DocumentoFiscalSapDto>? ToBusinessPartnerTax { get; set; }

    [JsonPropertyName("to_BusinessPartnerAddress")]
    public ODataResultsDto<EnderecoBusinessPartnerSapDto>? ToBusinessPartnerAddress { get; set; }
}

internal sealed class DocumentoFiscalSapDto
{
    public string? BPTaxType { get; set; }

    public string? BPTaxNumber { get; set; }

    public string? BPTaxLongNumber { get; set; }
}

internal sealed class EnderecoBusinessPartnerSapDto
{
    public string? AddressID { get; set; }

    public string? StreetName { get; set; }

    public string? CityName { get; set; }

    public string? Region { get; set; }

    public string? PostalCode { get; set; }

    public string? Country { get; set; }

    [JsonPropertyName("to_AddressUsage")]
    public ODataResultsDto<UsoEnderecoSapDto>? ToAddressUsage { get; set; }

    [JsonPropertyName("to_PhoneNumber")]
    public ODataResultsDto<TelefoneSapDto>? ToPhoneNumber { get; set; }

    [JsonPropertyName("to_EmailAddress")]
    public ODataResultsDto<EmailSapDto>? ToEmailAddress { get; set; }
}

internal sealed class UsoEnderecoSapDto
{
    public string? AddressID { get; set; }

    public string? AddressUsage { get; set; }

    public string? ValidityStartDate { get; set; }

    public string? ValidityEndDate { get; set; }
}

internal sealed class TelefoneSapDto
{
    public string? PhoneNumber { get; set; }
}

internal sealed class EmailSapDto
{
    public string? EmailAddress { get; set; }
}
