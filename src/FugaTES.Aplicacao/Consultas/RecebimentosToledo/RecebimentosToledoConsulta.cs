using FugaTES.Aplicacao.Servicos.Recebimento;
namespace FugaTES.Aplicacao.Consultas.RecebimentosToledo;

public sealed record RecebimentoToledoLinha(Guid ReciboId,string Origem,string Centro,string IdPesagem,
 DateTime DataPesagemLocal,DateTimeOffset RecebidoEm,string Estado,string? Admissao,string Contratos,int Itens,string Correlacao)
{
 public DevolutivaToledoSapSnapshot? DevolutivaSap {get;init;}
 public string EstadoSap => DevolutivaSap?.Estado switch
 {
  "PENDENTE" or "AGUARDANDO_REPROCESSAMENTO"=>"Na fila SAP",
  "EM_PROCESSAMENTO"=>"Enviando ao SAP",
  "CONCLUIDA"=>"Concluída conforme journal",
  "RESULTADO_INDETERMINADO"=>"Resultado SAP requer reconciliação",
  "FALHA_DEFINITIVA" or "AGUARDANDO_ACAO_MANUAL"=>"Falha / ação manual",
  null=>"Não iniciado / sem vínculo de devolutiva",
  _=>"Estado SAP não reconhecido — conferir journal"
 };
 public string HttpSap=>DevolutivaSap?.HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"Sem resposta HTTP registrada";
 public string ResultadoSap=>DevolutivaSap?.Codigo is "RecebimentoConfirmadoSap"?"SAP confirmou recebimento (201)":
  DevolutivaSap?.Codigo is "RecebimentoJaConfirmadoSap"?"Recebimento SAP confirmado por consulta; nenhum novo POST":
  DevolutivaSap?.Codigo is "RegistroSapExistenteDivergente"?"SAP ja possui esta pesagem com dados diferentes; nao repetir POST":
  DevolutivaSap?.Codigo==nameof(ResultadoToledoSapCodigo.AceiteHttpSemConfirmacaoEmpresarial)
  ?"Aceite HTTP recebido; confirmação empresarial SAP não comprovada":DevolutivaSap?.Codigo??"Nenhuma tentativa SAP vinculada";
 public string MotivoReenvio=>DevolutivaToledoSapPolicy.Avaliar(DevolutivaSap,false).Motivo;
}
public sealed record RecebimentosToledoSnapshot(IReadOnlyList<RecebimentoToledoLinha> Rows,bool HasMore,DateTimeOffset CapturadoEm);
// Stored JSONB document content: not reconstructed and not a claim of original wire-byte formatting.
public sealed record RecebimentoToledoDocumento(Guid ReciboId,string DocumentoJson,string FingerprintSha256);
public interface IRecebimentosToledoReadRepository
{
 Task<RecebimentosToledoSnapshot> ConsultarRecebimentosAsync(string? busca,CancellationToken ct);
 Task<RecebimentoToledoDocumento> ObterJsonRecebidoAsync(Guid reciboId,CancellationToken ct);
}
public static class RecebimentosToledoFiltro
{
 public static string? Normalizar(string? busca)
 {
  if(string.IsNullOrWhiteSpace(busca))return null;
  var value=busca.Trim();
  if(value.Length>128||(!value.All(char.IsAsciiDigit)&&(!Guid.TryParseExact(value,"D",out var id)||id==Guid.Empty)))
   throw new ArgumentException("Informe o contrato, ID da pesagem (somente digitos) ou ReciboId completo.");
  return value;
 }
}
