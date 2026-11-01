using FugaTES.Aplicacao.Modelos.Sd;

namespace FugaTES.Aplicacao.Contratos.Sd;

public static class EstadoDevolutivaSd
{
    public const string Correlacionada = "CORRELACIONADA";

    /// <summary>
    /// Chegou sem os campos que apontam a ordem. Fica visivel e intocada: adivinhar a ordem por
    /// parceiro, centro e data acerta no teste e erra quando ha duas ordens do mesmo dia.
    /// </summary>
    public const string SemCorrelacao = "SEM_CORRELACAO";
}

/// <summary>Uma devolutiva como a tela precisa ve-la.</summary>
public sealed record DevolutivaSdLinha(
    string IdPesagem, string Centro, DateTime DataPesagem,
    decimal PesoBruto, decimal PesoTara, decimal PesoLiquido,
    string? ChaveNatural, string? OrdemVenda, string? LinhaOrdem,
    string? Placa, string? NomeMotorista, string Estado,
    DateTimeOffset RecebidoEm, string PayloadJson)
{
    /// <summary>Protocolo da linha correlacionada, quando ela consta no registro de protocolos.</summary>
    public long? ProtocoloId { get; init; }
}

public interface IDevolutivasSdRepositorio
{
    Task<IReadOnlyList<DevolutivaSdLinha>> ListarAsync(int limite, CancellationToken ct);

    /// <summary>
    /// Grava o que chegou. Lanca <see cref="DevolutivaSdJaRecebidaException"/> quando o mesmo
    /// IdPesagem ja entrou - o Guardian reenvia, e reenvio nao pode virar segunda remessa.
    /// </summary>
    Task RegistrarAsync(DevolutivaPesagemSd devolutiva, CancellationToken ct);
}

public sealed class DevolutivaSdJaRecebidaException(string idPesagem)
    : Exception("A pesagem " + idPesagem + " já havia sido recebida.")
{
    public string IdPesagem { get; } = idPesagem;
}
