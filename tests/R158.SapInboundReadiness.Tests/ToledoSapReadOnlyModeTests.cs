using FugaTES.Callback.Http;
namespace FugaTES.Testes;
public sealed class ToledoSapReadOnlyModeTests
{
 static string[] Args(params string[] extras)=>[ToledoSapAuthenticationReadOnly.Flag,Path.GetTempPath(),..extras];
 [Fact] public void Normal_host_does_not_enter_the_administrative_SAP_check()=>Assert.False(ToledoSapAuthenticationReadOnly.Selected([]));
 [Theory][InlineData("--toledo-sap-auth-read-only")][InlineData("--toledo-sap-auth-read-only-forged")]
 public void Incomplete_or_forged_mode_cannot_boot_an_alternate_host(string mode)=>Assert.Throws<InvalidOperationException>(()=>ToledoSapAuthenticationReadOnly.Selected([mode]));
 [Fact] public void Extra_arguments_or_relative_secret_directory_are_rejected()
 {
  Assert.Throws<InvalidOperationException>(()=>ToledoSapAuthenticationReadOnly.Selected([ToledoSapAuthenticationReadOnly.Flag,"relative","DEV110"]));
  Assert.Throws<InvalidOperationException>(()=>ToledoSapAuthenticationReadOnly.Selected(Args("DEV110","--urls=http://0.0.0.0")));
 }

 /// <summary>
 /// O ambiente e OBRIGATORIO. Ate 05/11/2026 esta checagem tinha o DEV110 escrito dentro e
 /// aceitava dois argumentos: rodada na instalacao de QAS120 ela conferiria o DEV e imprimiria
 /// PASS. Quem a chamava com dois argumentos passa a ser recusado - falhar alto e melhor do que
 /// atestar o ambiente errado em silencio.
 /// </summary>
 [Fact] public void A_checagem_sem_ambiente_e_recusada()
  => Assert.Throws<InvalidOperationException>(()=>ToledoSapAuthenticationReadOnly.Selected(Args()));

 [Theory][InlineData("DEV110")][InlineData("QAS120")][InlineData("qas120")]
 public void A_checagem_aceita_cada_ambiente_aprovado(string ambiente)
  => Assert.True(ToledoSapAuthenticationReadOnly.Selected(Args(ambiente)));

 [Theory][InlineData("PRD")][InlineData("")][InlineData("110")][InlineData("QAS")]
 public void A_checagem_recusa_ambiente_que_nao_existe(string ambiente)
  => Assert.Throws<InvalidOperationException>(()=>ToledoSapAuthenticationReadOnly.Selected(Args(ambiente)));
}
