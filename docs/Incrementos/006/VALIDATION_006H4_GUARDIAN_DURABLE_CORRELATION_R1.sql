-- Somente leitura: inspecao estrutural. NAO EXECUTAR MIGRATION NESTE GATE.
BEGIN TRANSACTION READ ONLY;
SELECT column_name, data_type, is_nullable FROM information_schema.columns
 WHERE table_schema='fugates' AND table_name='contrato_sap_toledo_versao'
 AND column_name LIKE 'guardian_%' ORDER BY ordinal_position;
SELECT indexname FROM pg_indexes WHERE schemaname='fugates'
 AND tablename='contrato_sap_toledo_versao' AND indexname LIKE '%h4_guardian%'
 ORDER BY indexname;
ROLLBACK;
