using System.Text.Json.Serialization;
using FugaTES.Aplicacao.Modelos.Guardian;
namespace FugaTES.Aplicacao.Modelos.Mm;
public sealed record DadosProtocoloMm(
    [property:JsonPropertyName("id")] int Id,
    [property:JsonPropertyName("codigoItem")] string CodigoItem,
    [property:JsonPropertyName("codigoEmissor")] string CodigoEmissor,
    [property:JsonPropertyName("camposAdicionais")] IReadOnlyList<CampoAdicionalGuardianSet26> CamposAdicionais)
{
    [JsonPropertyName("numeroReferencia")] public string NumeroReferencia { get; init; } = string.Empty;
}
public sealed record ConfiguracaoProtocoloMm([property:JsonPropertyName("urlParaRetorno")] string UrlParaRetorno);
public sealed record ProtocoloPreCadastroMmRequest(
    [property:JsonPropertyName("configuracao")] ConfiguracaoProtocoloMm Configuracao,
    [property:JsonPropertyName("dadosProtocoloPreCadastro")] DadosProtocoloMm DadosProtocoloPreCadastro);
public static class EstadoProtocoloMm
{
    public const string Indeterminado="INDETERMINADO", Criado="CRIADO", Recusado="RECUSADO";
}
public sealed record ResultadoProtocoloMm(string Estado,int? Http,long? ProtocoloId,string Mensagem);
public sealed record RegistroProtocoloMm(Guid ReservaId,string Chave,string Pedido,string Item,string Centro,
    string JsonEnviado,string Sha256,string Estado,int? Http,long? ProtocoloId,string? Mensagem,DateTimeOffset AtualizadoUtc,string? SourceSha256=null);
public interface IEnviarProtocoloMmGateway
{
    Task<ResultadoProtocoloMm> EnviarAsync(byte[] payload,CancellationToken ct);
}
