namespace FugaTES.Aplicacao.Contratos.Sd;

/// <summary>Desfecho de um envio. Os tres valores sao os aceitos pela tabela.</summary>
public static class EstadoProtocoloSd
{
    public const string Criado = "CRIADO";
    public const string Recusado = "RECUSADO";

    /// <summary>
    /// O POST saiu e a resposta se perdeu. Ocupa a chave como qualquer outro: supor que nao
    /// chegou e o que cria protocolo duplicado no Guardian.
    /// </summary>
    public const string DesfechoDesconhecido = "DESFECHO_DESCONHECIDO";

    /// <summary>
    /// Se a linha pode ser enviada DE NOVO depois de um desfecho.
    ///
    /// So <see cref="Recusado"/> permite. A causa de uma recusa do Guardian costuma estar fora de
    /// nos e ser corrigivel: em 02/11/2026 a ordem 4237/10 foi recusada por falta de cadastro do
    /// emissor e do material. Cadastrados, reenviar e o caminho esperado - e, sem isto, destravar
    /// exigia editar o banco a mao.
    ///
    /// <see cref="Criado"/> e <see cref="DesfechoDesconhecido"/> continuam bloqueando: no primeiro
    /// o protocolo existe; no segundo ele PODE existir, e supor que nao existe e o que cria
    /// protocolo duplicado no Guardian.
    /// </summary>
    public static bool PermiteNovaTentativa(string? estado)
        => string.Equals(estado, Recusado, StringComparison.Ordinal);
}

/// <summary>O que se grava depois de um envio, com ou sem sucesso.</summary>
public sealed record ProtocoloSdEnvio(
    string ChaveNatural, string OrdemVenda, string LinhaOrdem, string Centro,
    string CodigoItemGuardian, string CodigoEmissorGuardian,
    long? ProtocoloId, string Estado, int HttpStatus, string PayloadSha256,
    string UrlRetorno, string? MensagemRecusa);

/// <summary>O que se le para decidir se uma linha ja virou protocolo.</summary>
public sealed record ProtocoloSdRegistrado(
    string ChaveNatural, long? ProtocoloId, string Estado, DateTimeOffset QuandoUtc, int HttpStatus);

/// <summary>
/// Registro duravel dos protocolos criados a partir de ordens de venda.
///
/// A regra que este contrato existe para sustentar: uma linha de ordem vira protocolo UMA vez.
/// Quem implementa tem de falhar quando nao consegue responder, e nunca devolver "nao encontrei"
/// por indisponibilidade - isso seria lido como "pode enviar".
/// </summary>
public interface IRegistroProtocolosSd
{
    Task<ProtocoloSdRegistrado?> ProcurarAsync(string chaveNatural, CancellationToken ct);

    Task<IReadOnlyList<ProtocoloSdRegistrado>> LerAsync(CancellationToken ct);

    /// <summary>
    /// Grava o envio. Lanca <see cref="ProtocoloSdJaRegistradoException"/> se a chave ja estiver
    /// ocupada - a corrida entre duas janelas termina no indice unico do banco, nao numa
    /// conferencia que pode ficar velha entre a leitura e a escrita.
    /// </summary>
    Task RegistrarAsync(ProtocoloSdEnvio envio, CancellationToken ct);

    /// <summary>
    /// Fecha o desfecho de uma chave ja reservada. O fluxo e: reservar antes de enviar, enviar,
    /// depois fechar. Assim uma queda no meio do envio deixa a linha ocupada como
    /// DESFECHO_DESCONHECIDO, e nao livre para um segundo POST.
    /// </summary>
    Task AtualizarDesfechoAsync(string chaveNatural, long? protocoloId, string estado,
        int httpStatus, string? mensagemRecusa, CancellationToken ct);
}

public sealed class ProtocoloSdJaRegistradoException(string chave)
    : Exception("Já existe protocolo para a linha " + chave + ".")
{
    public string ChaveNatural { get; } = chave;
}
