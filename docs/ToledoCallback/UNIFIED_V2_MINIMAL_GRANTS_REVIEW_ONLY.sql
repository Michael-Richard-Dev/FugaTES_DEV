-- DISTINCT APPROVAL REQUIRED. NOT EXECUTED. Target/role/owner/absence of excess privileges must be attested first.
-- No UPDATE, DELETE, TRUNCATE, schema CREATE, role membership or sequence privileges.
BEGIN;
SET LOCAL lock_timeout='1s';
DO $guard$
BEGIN
 IF current_setting('cluster_name') IS DISTINCT FROM '18/fugatesacmdev'
 OR current_setting('port') IS DISTINCT FROM '5433' OR current_database() IS DISTINCT FROM 'fugates_dev'
 OR pg_is_in_recovery() OR session_user IS DISTINCT FROM 'postgres' THEN
 RAISE EXCEPTION 'TOLEDO_ACL_TARGET_MISMATCH'; END IF;
END $guard$;
SET LOCAL ROLE fugates_dev_owner;
GRANT INSERT(recibo_id,origem,centro,id_pesagem,data_pesagem_local,documento_json,fingerprint_sha256,estado,recebido_em,origem_autenticada),
 SELECT(recibo_id,recebido_em,fingerprint_sha256,origem,centro,id_pesagem) ON fugates.toledo_pesagem_recebida TO fugates_acm_hml_app;
GRANT INSERT(recibo_id,indice,nr_indice_total,contrato_acm,id_parceiro,documento_item,integracao_id,versao_h4,estado_correlacao),
 SELECT(recibo_id,indice,estado_correlacao,integracao_id,versao_h4) ON fugates.toledo_pesagem_item TO fugates_acm_hml_app;
GRANT INSERT(entrega_id,recibo_id,origem_autenticada,recebido_em,resultado) ON fugates.toledo_pesagem_entrega TO fugates_acm_hml_app;
COMMIT;
