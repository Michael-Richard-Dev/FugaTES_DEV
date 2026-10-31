-- 007S1 - desfaz a tabela de protocolos SD.
--
-- ATENCAO: isto apaga o registro de quais linhas de ordem JA VIRARAM protocolo no Guardian.
-- Os protocolos continuam existindo la - o rollback nao os desfaz. Rodar isto com o fluxo SD
-- ativo faz o aplicativo perder a memoria dos envios e recriar protocolos duplicados.
--
-- Antes de executar, exporte o conteudo:
--   \copy (select * from fugates.protocolo_sd_ordem_venda) to 'protocolo_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
drop table fugates.protocolo_sd_ordem_venda;
commit;
