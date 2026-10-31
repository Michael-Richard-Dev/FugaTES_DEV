-- Target-only additive column SELECT. Required by the canonical monitor read repository.
-- No writer grants, no data mutation, no default privileges or role membership changes.
-- Apply once after verified existing backup and independent review; never blind retry after unknown COMMIT.
BEGIN;
SET TRANSACTION READ WRITE;
SET LOCAL statement_timeout='10s';
SET LOCAL lock_timeout='3s';
SET LOCAL search_path=pg_catalog;
DO $guard$
BEGIN
 IF current_database()<>'fugates_dev' OR current_setting('port')<>'5433'
  OR current_setting('cluster_name')<>'18/fugatesacmdev' OR pg_is_in_recovery()
  OR current_setting('server_version_num')::integer NOT BETWEEN 180000 AND 189999
  OR session_user<>'postgres' OR current_user<>'postgres' THEN
  RAISE EXCEPTION 'UNIFIED_DEV_READ_ACL_TARGET_REJECTED';
 END IF;
 IF NOT EXISTS(select 1 from pg_roles where rolname='fugates_acm_hml_app' and rolcanlogin and not rolsuper and not rolcreaterole and not rolcreatedb and not rolbypassrls)
  OR pg_has_role('fugates_acm_hml_app','fugates_dev_owner','SET')
  OR EXISTS(select 1 from pg_class c where c.oid in ('fugates.integracao'::regclass,'fugates.tentativa_integracao'::regclass,'fugates.auditoria_integracao'::regclass)
    and pg_get_userbyid(c.relowner)<>'fugates_dev_owner') THEN
  RAISE EXCEPTION 'UNIFIED_DEV_READ_ACL_ROLE_OR_OWNER_REJECTED';
 END IF;
END; $guard$;
SET LOCAL ROLE fugates_dev_owner;
GRANT SELECT (atualizado_em) ON fugates.integracao TO fugates_acm_hml_app;
GRANT SELECT (tipo_disparo,fase_envio,criada_em,envio_iniciado_em,resultado_recebido_em)
 ON fugates.tentativa_integracao TO fugates_acm_hml_app;
GRANT SELECT (id,integracao_id,evento,criada_em,fencing_token,tentativa_id)
 ON fugates.auditoria_integracao TO fugates_acm_hml_app;
DO $postcheck$
BEGIN
 IF EXISTS(select 1 from (values
  ('fugates.integracao','atualizado_em'),
  ('fugates.tentativa_integracao','tipo_disparo'),('fugates.tentativa_integracao','fase_envio'),
  ('fugates.tentativa_integracao','criada_em'),('fugates.tentativa_integracao','envio_iniciado_em'),
  ('fugates.tentativa_integracao','resultado_recebido_em'),
  ('fugates.auditoria_integracao','id'),('fugates.auditoria_integracao','integracao_id'),
  ('fugates.auditoria_integracao','evento'),('fugates.auditoria_integracao','criada_em'),
  ('fugates.auditoria_integracao','fencing_token'),('fugates.auditoria_integracao','tentativa_id')
 ) v(tbl,col) where not has_column_privilege('fugates_acm_hml_app',v.tbl,v.col,'SELECT')) THEN
  RAISE EXCEPTION 'UNIFIED_DEV_READ_ACL_POSTCHECK_FAILED';
 END IF;
END; $postcheck$;
COMMIT;
