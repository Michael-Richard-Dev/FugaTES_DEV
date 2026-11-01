-- 007S2 - desfaz a tabela de devolutivas SD.
--
-- ATENCAO: apaga o registro do que o Guardian ja devolveu, inclusive a marca de quais pesagens
-- ja foram processadas. Com o fluxo ativo, um reenvio do Guardian passaria a ser tratado como
-- novo. Exporte antes:
--   \copy (select * from fugates.devolutiva_sd_pesagem) to 'devolutiva_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
drop table fugates.devolutiva_sd_pesagem;
commit;
