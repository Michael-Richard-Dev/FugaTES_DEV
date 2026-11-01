-- 007S3 - desfaz a tabela de remessas SD e a coluna de setor no protocolo.
--
-- ATENCAO: apaga o registro de quais pesagens JA VIRARAM remessa no SAP. As remessas continuam
-- la - o rollback nao as desfaz - e o fluxo passaria a criar remessa duplicada para a mesma
-- pesagem. Exporte antes:
--   \copy (select * from fugates.remessa_sd) to 'remessa_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
alter table fugates.protocolo_sd_ordem_venda drop constraint if exists ck_protocolo_sd_setor;
alter table fugates.protocolo_sd_ordem_venda drop column if exists setor_atividade;
drop table fugates.remessa_sd;
commit;
