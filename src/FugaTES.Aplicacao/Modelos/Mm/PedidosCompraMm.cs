namespace FugaTES.Aplicacao.Modelos.Mm;

// Coleção somente leitura: identificadores preservam zeros e quantidade não passa por double.
public sealed record PedidoCompraMmLeitura(
    string Empresa, string Pedido, string Item, string TipoPedido, string Centro,
    string Material, string GrupoMaterial, decimal Quantidade, string Unidade,
    string Fornecedor, string EstadoSap, string Json, DateOnly? DataCriacao = null);

public sealed record MmConsultaResultado(bool Sucesso, string Codigo, int? Http,
    int Paginas, IReadOnlyList<PedidoCompraMmLeitura> Itens);

public interface IPedidosCompraMmConsulta
{
    Task<MmConsultaResultado> ConsultarAsync(CancellationToken cancellationToken);
}
