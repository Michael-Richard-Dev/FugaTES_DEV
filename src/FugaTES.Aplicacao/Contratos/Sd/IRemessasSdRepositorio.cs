namespace FugaTES.Aplicacao.Contratos.Sd;

public static class EstadoRemessaSd
{
    public const string Criada = "CRIADA";
    public const string Recusada = "RECUSADA";

    /// <summary>
    /// O POST saiu e a resposta se perdeu. Ocupa a pesagem como qualquer outro desfecho: a
    /// remessa pode existir no SAP, e supor que nao existe e o que cria a segunda.
    /// </summary>
    public const string DesfechoDesconhecido = "DESFECHO_DESCONHECIDO";
}

/// <summary>
/// Passo 4: ajuste da remessa ja criada. Nulo na linha significa "ainda nao ajustada" - e por
/// isso que a coluna nasceu nulavel na 007S4, em vez de com um estado inicial inventado.
/// </summary>
public static class EstadoAjusteRemessaSd
{
    public const string Ajustada = "AJUSTADA";
    public const string Recusada = "RECUSADA";
    public const string DesfechoDesconhecido = "DESFECHO_DESCONHECIDO";
}

/// <summary>O que se grava ao tentar criar a remessa, com ou sem sucesso.</summary>
public sealed record RemessaSdRegistro(
    string IdPesagem, string ChaveNatural, string OrdemVenda, string LinhaOrdem,
    string SetorAtividade, string Quantidade, string Unidade,
    string? NumeroRemessa, string Estado, int HttpStatus, string PayloadSha256, string? MensagensSap);

/// <summary>
/// Passo 5: gravacao do texto ZNFE. Nulo significa "texto ainda nao tocado por nos" - o texto da
/// ordem continua la, intacto.
/// </summary>
public static class EstadoTextoNfeSd
{
    public const string Gravado = "GRAVADO";
    public const string Recusado = "RECUSADO";
    public const string DesfechoDesconhecido = "DESFECHO_DESCONHECIDO";
}

/// <summary>As duas etapas do passo 6. Nomes fixos: e por eles que o repositorio escolhe colunas.</summary>
public static class EtapaExpedicaoSd
{
    public const string Picking = "picking";
    public const string Saida = "saida";
}

/// <summary>Passo 6, parte 1: confirmacao do picking de todos os itens.</summary>
public static class EstadoPickingSd
{
    public const string Confirmado = "CONFIRMADO";
    public const string Recusado = "RECUSADO";
    public const string DesfechoDesconhecido = "DESFECHO_DESCONHECIDO";
}

/// <summary>
/// Passo 6, parte 2: saida de mercadoria. E o passo irreversivel do fluxo - move estoque e gera
/// documento de material. Qualquer estado nao nulo aqui impede uma segunda tentativa pela tela,
/// inclusive <see cref="DesfechoDesconhecido"/>: supor que nao saiu e o que cria a segunda saida.
/// </summary>
public static class EstadoSaidaSd
{
    public const string Postada = "POSTADA";
    public const string Recusada = "RECUSADA";
    public const string DesfechoDesconhecido = "DESFECHO_DESCONHECIDO";
}

/// <summary>O que a tela precisa ver sobre uma remessa.</summary>
public sealed record RemessaSdLinha(
    string IdPesagem, string ChaveNatural, string? NumeroRemessa, string Estado,
    string Quantidade, string Unidade, string SetorAtividade,
    int HttpStatus, string? MensagensSap, DateTimeOffset CriadoEm)
{
    /// <summary>Passo 4. Nulo quando a remessa ainda nao foi ajustada.</summary>
    public string? AjusteEstado { get; init; }

    public string? AjusteMensagens { get; init; }

    /// <summary>Passo 5. Nulo quando o texto da remessa ainda nao foi tocado.</summary>
    public string? TextoEstado { get; init; }

    public string? TextoMensagens { get; init; }

    /// <summary>Passo 6. Nulos quando a remessa ainda nao foi expedida.</summary>
    public string? PickingEstado { get; init; }

    public string? SaidaEstado { get; init; }

    public string? SaidaMensagens { get; init; }
}

public interface IRemessasSdRepositorio
{
    Task<IReadOnlyList<RemessaSdLinha>> ListarAsync(int limite, CancellationToken ct);

    Task<RemessaSdLinha?> ProcurarPorPesagemAsync(string idPesagem, CancellationToken ct);

    /// <summary>
    /// Reserva a pesagem antes do POST. Lanca <see cref="RemessaSdJaCriadaException"/> quando ela
    /// ja foi usada - a corrida termina no indice unico do banco, nao numa leitura previa.
    /// </summary>
    Task ReservarAsync(RemessaSdRegistro registro, CancellationToken ct);

    Task FecharDesfechoAsync(string idPesagem, string? numeroRemessa, string estado,
        int httpStatus, string? mensagensSap, CancellationToken ct);

    /// <summary>
    /// Reserva o passo 4 antes do PATCH, gravando <see cref="EstadoAjusteRemessaSd.DesfechoDesconhecido"/>.
    /// Lanca <see cref="AjusteRemessaSdJaFeitoException"/> quando a remessa ja tem ajuste: o
    /// cabecalho so recebe o peso de UMA pesagem.
    /// </summary>
    Task ReservarAjusteAsync(string idPesagem, string payloadSha256, CancellationToken ct);

    Task FecharAjusteAsync(string idPesagem, string estado, int httpStatus,
        string? mensagens, CancellationToken ct);

    /// <summary>
    /// Reserva o passo 5 antes de gravar o texto. Lanca <see cref="TextoNfeSdJaGravadoException"/>
    /// quando a remessa ja teve o texto gravado por nos: regravar correria o risco de empurrar de
    /// novo o que ja entrou.
    /// </summary>
    Task ReservarTextoAsync(string idPesagem, string textoSha256, CancellationToken ct);

    Task FecharTextoAsync(string idPesagem, string estado, int httpStatus, bool criou,
        string? mensagens, CancellationToken ct);

    /// <summary>
    /// Reserva uma etapa do passo 6 antes do POST. <paramref name="etapa"/> e "picking" ou
    /// "saida". Lanca <see cref="ExpedicaoSdJaFeitaException"/> quando ja ha desfecho gravado -
    /// no caso da saida, isso e o que impede uma segunda saida de mercadoria.
    /// </summary>
    Task ReservarExpedicaoAsync(string idPesagem, string etapa, CancellationToken ct);

    Task FecharExpedicaoAsync(string idPesagem, string etapa, string estado, int httpStatus,
        string? mensagens, CancellationToken ct);
}

public sealed class ExpedicaoSdJaFeitaException(string idPesagem, string etapa)
    : Exception("A etapa " + etapa + " da pesagem " + idPesagem + " já tem desfecho registrado.")
{
    public string IdPesagem { get; } = idPesagem;

    public string Etapa { get; } = etapa;
}

public sealed class TextoNfeSdJaGravadoException(string idPesagem)
    : Exception("O texto da remessa da pesagem " + idPesagem + " já foi gravado.")
{
    public string IdPesagem { get; } = idPesagem;
}

public sealed class AjusteRemessaSdJaFeitoException(string idPesagem)
    : Exception("A remessa da pesagem " + idPesagem + " já passou pelo ajuste.")
{
    public string IdPesagem { get; } = idPesagem;
}

public sealed class RemessaSdJaCriadaException(string idPesagem)
    : Exception("A pesagem " + idPesagem + " já gerou remessa.")
{
    public string IdPesagem { get; } = idPesagem;
}
