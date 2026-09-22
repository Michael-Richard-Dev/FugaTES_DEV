namespace FugaTES.Aplicacao.Modelos.Guardian;

public sealed record EmissorGuardianSet26(string? Codigo, string Descricao, string RazaoSocial, string Estado, string TipoDocumento);
public sealed record TransportadoraGuardianSet26(string? Codigo, string Descricao, string RazaoSocial);
public sealed record CampoAdicionalGuardianSet26(int Numero, string Valor);
public sealed record PreCadastroRecebimentoGuardianSet26(int Id, string CodigoFluxo, string CodigoItem, string CodigoEmissor, string CodigoTransportadora, string CodigoContrato, int IdAgenda, string NumeroReferencia, string UrlParaRetorno, IReadOnlyCollection<CampoAdicionalGuardianSet26> CamposAdicionais);
public sealed record GuardianSet26Options(string EndpointEspecifico, string UrlParaRetorno);
public sealed record ProtocoloGuardianSet26(string Valor);
