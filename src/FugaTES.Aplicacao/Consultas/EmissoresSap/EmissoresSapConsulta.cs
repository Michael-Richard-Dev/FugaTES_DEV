namespace FugaTES.Aplicacao.Consultas.EmissoresSap;
public sealed record EmissoresSapFiltro(string Codigo="",string Nome="");
public sealed record EmissorSapLeitura(string Codigo,string Nome,string Pessoa,bool Cliente,bool Fornecedor,bool? Bloqueado,string Grupo)
{
 public string Funcoes=>Cliente&&Fornecedor?"Cliente e fornecedor":Cliente?"Cliente":"Fornecedor";
 public string Situacao=>Bloqueado is true?"Bloqueado":Bloqueado is false?"Sem bloqueio geral":"Não informado";
}
public sealed record EmissoresSapPagina(IReadOnlyList<EmissorSapLeitura> Registros,string? Continuacao,long? Total,int Inicio,DateTimeOffset LidoEm);
public sealed record EmissorSapDado(string Grupo,string Campo,string Valor);
public sealed record EmissorSapDetalhe(string Codigo,string Json,IReadOnlyList<EmissorSapDado> Dados,DateTimeOffset LidoEm);
public interface IEmissoresSapConsulta
{
 Task<EmissoresSapPagina> ConsultarAsync(EmissoresSapFiltro filtro,string? continuacao,CancellationToken ct);
 Task<EmissorSapDetalhe> ObterDetalheAsync(string bp,CancellationToken ct);
}
public sealed class EmissoresSapException(string codigo,int? httpStatus=null):Exception(codigo)
{public string Codigo{get;}=codigo;public int? HttpStatus{get;}=httpStatus;}
