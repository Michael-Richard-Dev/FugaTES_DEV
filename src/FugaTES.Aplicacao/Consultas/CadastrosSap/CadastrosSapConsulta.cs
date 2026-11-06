namespace FugaTES.Aplicacao.Consultas.CadastrosSap;
public enum CategoriaCadastroSap{Materiais,Transportadoras}
public sealed record CadastrosSapFiltro(string Codigo="",string Nome="");
public sealed record CadastroSapLeitura(string Codigo,string Nome,string Tipo,string Grupo,string Informacoes,string Situacao);
public sealed record CadastrosSapPagina(IReadOnlyList<CadastroSapLeitura> Registros,string? Continuacao,long Total,int Inicio,DateTimeOffset LidoEm);
public sealed record CadastroSapDado(string Grupo,string Campo,string Valor);
public sealed record CadastroSapDetalhe(string Codigo,string Json,IReadOnlyList<CadastroSapDado> Dados,DateTimeOffset LidoEm);
public interface ICadastroSapConsulta
{
 CategoriaCadastroSap Categoria{get;}
 Task<CadastrosSapPagina> ConsultarAsync(CadastrosSapFiltro filtro,string? continuacao,CancellationToken ct);
 Task<CadastroSapDetalhe> ObterDetalheAsync(string codigo,CancellationToken ct);
}
public sealed class CadastroSapException(string codigo,int? httpStatus=null):Exception(codigo)
{public string Codigo{get;}=codigo;public int? HttpStatus{get;}=httpStatus;}
