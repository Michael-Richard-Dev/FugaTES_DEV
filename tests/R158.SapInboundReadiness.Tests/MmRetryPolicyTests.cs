using System.Security.Cryptography;
using System.Text;
using FugaTES.Aplicacao.Modelos.Mm;
using FugaTES.Aplicacao.Servicos.Mm;
namespace FugaTES.Testes;
public sealed class MmRetryPolicyTests
{
 public static PedidoCompraMmLeitura Source()=>new("3000","4500002991","10","NB","3008","1000247","000010004",9000,"M3","20000162","05","{}",new DateOnly(2026,11,3));
 public static RegistroProtocoloMm Record(){var s=Source();var j=MapeadorProtocoloMm.Serializar(MapeadorProtocoloMm.Montar(s,"https://example.invalid/mm"));return new(Guid.NewGuid(),MapeadorProtocoloMm.ChaveNatural(s),s.Pedido,s.Item,s.Centro,j,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(j))),EstadoProtocoloMm.Recusado,null,null,"MM_PRECHECK_MASTERS_MISSING",DateTimeOffset.UtcNow,MapeadorProtocoloMm.SourceHash(s));}
 [Fact] public void Correctible_pre_post_failure_can_request_retry(){Assert.True(MmRetryPolicy.Evaluate(new(Source(),Record(),true,false),[]).Allowed);}
 [Theory][InlineData("CRIADO",201)][InlineData("INDETERMINADO",0)][InlineData("RECUSADO",400)][InlineData("RECUSADO",401)][InlineData("RECUSADO",500)]
 public void Terminal_unknown_or_unclassified_HTTP_error_never_gets_blind_retry(string state,int status){var r=Record() with{Estado=state,Http=status==0?null:status,Mensagem="UNCLASSIFIED"};Assert.False(MmRetryPolicy.Evaluate(new(Source(),r,true,false),[]).Allowed);}
 [Theory][InlineData(false,false)][InlineData(true,true)] public void Stale_read_or_existing_command_blocks(bool fresh,bool pending){Assert.False(MmRetryPolicy.Evaluate(new(Source(),Record(),fresh,pending),[]).Allowed);}
 [Fact] public void Changed_SAP_source_and_wrong_scope_are_blocked(){Assert.False(MmRetryPolicy.Evaluate(new(Source() with{Material="NEW"},Record(),true,false),[]).Allowed);Assert.False(MmRetryPolicy.Evaluate(new(Source() with{EstadoSap="02"},Record(),true,false),[]).Allowed);}
 [Fact] public void Legacy400_needs_exact_protected_evidence_not_just_HTTP_status(){var r=Record() with{Http=400,Mensagem="Guardian refused"};var proof=new MmRetryLegacyEvidence(r.ReservaId,r.SourceSha256!,r.Sha256,new string('A',64)){RejectedUtc=r.AtualizadoUtc};Assert.True(MmRetryPolicy.Evaluate(new(Source(),r,true,false),[proof]).Allowed);Assert.False(MmRetryPolicy.Evaluate(new(Source(),r,true,false),[proof with{PayloadSha256=new string('B',64)}]).Allowed);}
 [Fact] public void Legacy_evidence_cannot_authorize_a_new_refusal_after_another_POST()
 {var r=Record() with{Http=400,Mensagem="original"};var proof=new MmRetryLegacyEvidence(r.ReservaId,r.SourceSha256!,r.Sha256,new string('A',64)){RejectedUtc=r.AtualizadoUtc};var later=r with{AtualizadoUtc=r.AtualizadoUtc.AddMinutes(1),Mensagem="different refusal"};Assert.False(MmRetryPolicy.Evaluate(new(Source(),later,true,false),[proof]).Allowed);}
 [Fact] public void Confirmed_remote_protocol_always_blocks(){Assert.False(MmRetryPolicy.Evaluate(new(Source(),Record() with{ProtocoloId=99},true,false),[]).Allowed);}
}
