using System.Text.Json.Serialization;

namespace FugaTES.Integracao.SAP.Dtos.Pesagem;

public sealed record PesagemSapWireDto(
    [property: JsonPropertyName("SistemaBalanca")] string SistemaBalanca,
    [property: JsonPropertyName("Centro")] string Centro,
    [property: JsonPropertyName("TipoPesagem")] string TipoPesagem,
    [property: JsonPropertyName("IdPesagem")] string IdPesagem,
    [property: JsonPropertyName("DataPesagem")] string? DataPesagem,
    [property: JsonPropertyName("UsuarioPesagem")] string? UsuarioPesagem,
    [property: JsonPropertyName("UsuarioClassificacao")] string? UsuarioClassificacao,
    [property: JsonPropertyName("IdPlanejamento")] string? IdPlanejamento,
    // ACM uses its attested CHAR(10); legacy non-ACM serialization retains its integer.
    [property: JsonPropertyName("IdAgendamento")] object? IdAgendamento,
    [property: JsonPropertyName("IdTransportadora")] string? IdTransportadora,
    [property: JsonPropertyName("FreteTonelada")] decimal? FreteTonelada,
    [property: JsonPropertyName("PesoBruto")] decimal PesoBruto,
    [property: JsonPropertyName("PesoTara")] decimal PesoTara,
    [property: JsonPropertyName("PesoAntesDesconto")] decimal PesoAntesDesconto,
    [property: JsonPropertyName("PesoAposDesconto")] decimal? PesoAposDesconto,
    [property: JsonPropertyName("NomeMotorista")] string? NomeMotorista,
    [property: JsonPropertyName("CpfMotorista")] string? CpfMotorista,
    [property: JsonPropertyName("Placa")] string? Placa,
    [property: JsonPropertyName("TipoOperacao")] short TipoOperacao,
    [property: JsonPropertyName("Observacao")] string? Observacao,
    [property: JsonPropertyName("Material")] string Material,
    [property: JsonPropertyName("TabelaQualidadeId")] string? TabelaQualidadeId,
    [property: JsonPropertyName("TabelaQualidadeVersao")] string? TabelaQualidadeVersao,
    [property: JsonPropertyName("TabQualVersValidade")] string? TabQualVersValidade,
    [property: JsonPropertyName("to_QualidPes")] IReadOnlyCollection<QualidadePesagemSapWireDto> Qualidades,
    [property: JsonPropertyName("to_Pesagem")] IReadOnlyCollection<ItemPesagemSapWireDto> Pesagens);

public sealed record QualidadePesagemSapWireDto(
    [property: JsonPropertyName("Tipo")] string Tipo,
    [property: JsonPropertyName("Grupo")] string? Grupo,
    [property: JsonPropertyName("ValorAferido")] decimal ValorAferido);

public sealed record ItemPesagemSapWireDto(
    [property: JsonPropertyName("Indice")] int Indice,
    [property: JsonPropertyName("NrIndiceTotal")] int NrIndiceTotal,
    [property: JsonPropertyName("IdParceiro")] string IdParceiro,
    [property: JsonPropertyName("IdParceiroEntrega")] string? IdParceiroEntrega,
    [property: JsonPropertyName("ObservacaoPesagem")] string? ObservacaoPesagem,
    [property: JsonPropertyName("ParceiroCpfCnpj")] string? ParceiroCpfCnpj,
    [property: JsonPropertyName("ParceiroNome")] string? ParceiroNome,
    [property: JsonPropertyName("ParceiroEntregaNome")] string? ParceiroEntregaNome,
    [property: JsonPropertyName("ContratoACM")] string ContratoAcm,
    [property: JsonPropertyName("OrdemVendas")] string? OrdemVendas,
    [property: JsonPropertyName("Remessa")] string? Remessa,
    [property: JsonPropertyName("PedidoCompras")] string? PedidoCompras,
    [property: JsonPropertyName("TipoDocumento")] string? TipoDocumento,
    [property: JsonPropertyName("PesoAntesDesconto")] decimal PesoAntesDesconto,
    [property: JsonPropertyName("PesoAposDesconto")] decimal? PesoAposDesconto,
    [property: JsonPropertyName("PercentualDesdobro")] decimal? PercentualDesdobro,
    [property: JsonPropertyName("ChaveAcessoNFe")] string? ChaveAcessoNFe,
    [property: JsonPropertyName("NumeroNF")] string? NumeroNf,
    [property: JsonPropertyName("SerieNF")] string? SerieNf,
    [property: JsonPropertyName("DataNf")] string? DataNf,
    [property: JsonPropertyName("VlrUnitarioNF")] decimal? ValorUnitarioNf,
    [property: JsonPropertyName("VlrTotalNF")] decimal? ValorTotalNf,
    [property: JsonPropertyName("QuantidadeNF")] decimal? QuantidadeNf,
    [property: JsonPropertyName("CPFCNPJTerceiro")] string? CpfCnpjTerceiro,
    [property: JsonPropertyName("NomeTerceiro")] string? NomeTerceiro,
    [property: JsonPropertyName("NrProtocolo")] string? NumeroProtocolo,
    [property: JsonPropertyName("DtProtocolo")] string? DataProtocolo,
    [property: JsonPropertyName("HrProtocolo")] string? HoraProtocolo);
