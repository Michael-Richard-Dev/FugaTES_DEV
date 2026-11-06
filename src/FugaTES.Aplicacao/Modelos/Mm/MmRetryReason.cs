namespace FugaTES.Aplicacao.Modelos.Mm;
public static class MmRetryReason
{
 public static bool IsPublic(string? code)=>code is "MM_RETRY_ALREADY_CREATED" or "MM_RETRY_ALREADY_PENDING" or "MM_RETRY_SOURCE_CHANGED" or "MM_RETRY_STALE_SAP" or "MM_RETRY_SAP_SCOPE_REJECTED" or "MM_RETRY_OUTCOME_UNCERTAIN" or "MM_RETRY_CAUSE_NOT_PROVEN_CORRECTED" or "MM_PRECHECK_MASTERS_MISSING" or "MM_PRECHECK_EMITTER_MISSING" or "MM_PRECHECK_MATERIAL_MISSING" or "MM_PRECHECK_MASTER_INACTIVE" or "MM_PRECHECK_ADMISSION_REVOKED" or "MM_PRECHECK_MASTER_UNVERIFIED" or "MM_RETRY_PREVIEW_CHANGED" or "MM_RETRY_LEASE_LOST_BEFORE_POST" or "MM_RETRY_PRE_POST_BLOCKED" or "MM_RETRY_EXTERNAL_UNCONFIRMED" or "MM_RETRY_LEASE_LOST_NO_REPLAY" or "MM_RETRY_RESULT_UNCONFIRMED" or "MM_RETRY_GUARDIAN_REJECTED";
 public static string Describe(string code)=>code switch
 {
  "MM_RETRY_ALREADY_CREATED"=>"Protocolo já criado no Guardian; novo envio bloqueado.",
  "MM_RETRY_ALREADY_PENDING"=>"Já há comando pendente ou processamento deste item.",
  "MM_RETRY_SOURCE_CHANGED"=>"Dados SAP ou documento reservado mudaram; conferir a integração antes de reenviar.",
  "MM_RETRY_STALE_SAP"=>"A leitura SAP não está atualizada; aguarde uma coleta completa.",
  "MM_RETRY_SAP_SCOPE_REJECTED"=>"Pedido/item não atende empresa, centro, data ou liberação SAP05.",
  "MM_RETRY_OUTCOME_UNCERTAIN"=>"Resultado externo desconhecido; é necessária reconciliação, sem novo POST.",
  "MM_RETRY_CAUSE_NOT_PROVEN_CORRECTED"=>"A causa da recusa ainda não tem correção comprovada; HTTP400 isolado não libera nova tentativa.",
  "MM_PRECHECK_MASTERS_MISSING"=>"Emissor e material não foram encontrados no Guardian. Cadastre ambos e solicite nova verificação.",
  "MM_PRECHECK_EMITTER_MISSING"=>"Emissor não encontrado no Guardian. Corrija o cadastro e solicite nova verificação.",
  "MM_PRECHECK_MATERIAL_MISSING"=>"Material não encontrado no Guardian. Corrija o cadastro e solicite nova verificação.",
  "MM_PRECHECK_MASTER_INACTIVE"=>"Há cadastro inativo no Guardian; o envio continua bloqueado.",
  "MM_PRECHECK_ADMISSION_REVOKED"=>"A coleta ou a lease mudou antes do envio; nenhum protocolo foi enviado.",
  "MM_PRECHECK_MASTER_UNVERIFIED"=>"Não foi possível comprovar os cadastros no Guardian; nenhum protocolo foi enviado.",
  "MM_RETRY_PREVIEW_CHANGED"=>"A integração mudou após a prévia; atualize a consulta.",
  "MM_RETRY_LEASE_LOST_BEFORE_POST" or "MM_RETRY_PRE_POST_BLOCKED"=>"O motor bloqueou a tentativa antes do POST; confira as condições operacionais.",
  "MM_RETRY_EXTERNAL_UNCONFIRMED" or "MM_RETRY_LEASE_LOST_NO_REPLAY" or "MM_RETRY_RESULT_UNCONFIRMED"=>"Resultado não confirmado; reconciliar sem repetir o POST.",
  "MM_RETRY_GUARDIAN_REJECTED"=>"Guardian recusou a nova tentativa; conferir o HTTP e o código seguro da resposta.",
  _=>"Operação MM bloqueada; conferir a evidência da integração."
 };
}
