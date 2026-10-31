using System.Text.Json.Serialization;
namespace FugaTES.Aplicacao.Servicos.Recebimento;

// Continuous mode is explicit, never inferred from a missing/malformed expiry.
public static class ToledoSapActivationScope
{
 public static bool Valid(DateTimeOffset start,DateTimeOffset end,bool continuous)=>start!=default&&start.Offset==TimeSpan.Zero&&
  (continuous?end==default:end.Offset==TimeSpan.Zero&&end>start&&end-start<=TimeSpan.FromDays(1));
 public static bool Active(DateTimeOffset start,DateTimeOffset end,bool continuous,DateTimeOffset now)=>
  Valid(start,end,continuous)&&now>=start&&(continuous||now<end);
}
// The lower admission cutoff remains mandatory in both modes; no historical inbox replay.
public sealed record ToledoSapAdmissionWindow(DateTimeOffset InicioUtc,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]DateTimeOffset FimUtc,
 string OrigemAdmitida,string ReferenciaAutorizacao,bool DocumentoCompletoAprovado=false)
{
 public bool ContinuoAutorizado {get;init;}
 public bool Valida=>ToledoSapActivationScope.Valid(InicioUtc,FimUtc,ContinuoAutorizado)&&
  !string.IsNullOrWhiteSpace(OrigemAdmitida)&&OrigemAdmitida.Length<=160&&!OrigemAdmitida.Any(char.IsControl)&&
  !string.IsNullOrWhiteSpace(ReferenciaAutorizacao)&&ReferenciaAutorizacao.Length<=160&&!ReferenciaAutorizacao.Any(char.IsControl);
 public bool Admite(DateTimeOffset received,string source)=>Valida&&received>=InicioUtc&&(ContinuoAutorizado||received<FimUtc)&&source==OrigemAdmitida;
}
