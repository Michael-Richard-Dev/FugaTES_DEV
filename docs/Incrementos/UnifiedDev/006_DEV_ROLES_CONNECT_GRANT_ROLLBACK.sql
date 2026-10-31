-- R166 - ROLLBACK do 006: devolve as duas identidades DEV ao estado sem CONNECT.
--
-- Efeito: fugates_dev_read_app e fugates_sap_inbound_app voltam a nao conseguir conectar em
-- fugates_dev. A API 18762 so pode ser revertida para fugates_acm_hml_app ANTES disto, ou junto,
-- sob pena de ficar sem identidade capaz de conectar.
--
-- NAO toca fugates_acm_hml_app, fugates_dev_app, fugates_dev_owner nem PUBLIC.

\set ON_ERROR_STOP on

BEGIN;
SET TRANSACTION READ WRITE;
SET LOCAL statement_timeout='15s';
SET LOCAL lock_timeout='5s';
SET LOCAL search_path=pg_catalog;

DO $guarda$
BEGIN
  IF current_database() <> 'fugates_dev'
     OR current_setting('port') <> '5433'
     OR current_setting('cluster_name') <> '18/fugatesacmdev'
     OR pg_is_in_recovery()
     OR session_user <> 'postgres' OR current_user <> 'postgres' THEN
    RAISE EXCEPTION 'DEV_CONNECT_ROLLBACK_TARGET_REJECTED';
  END IF;
END; $guarda$;

REVOKE CONNECT ON DATABASE fugates_dev FROM fugates_dev_read_app;
REVOKE CONNECT ON DATABASE fugates_dev FROM fugates_sap_inbound_app;

DO $postcheck$
DECLARE restante text;
BEGIN
  SELECT string_agg(v.role, ', ') INTO restante
    FROM (VALUES ('fugates_dev_read_app'),('fugates_sap_inbound_app')) v(role)
   WHERE has_database_privilege(v.role, 'fugates_dev', 'CONNECT');
  IF restante IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_CONNECT_ROLLBACK_INCOMPLETE: %', restante;
  END IF;

  -- O que nao e deste delta tem que continuar intacto.
  IF NOT has_database_privilege('fugates_acm_hml_app', 'fugates_dev', 'CONNECT')
     OR NOT has_database_privilege('fugates_dev_app', 'fugates_dev', 'CONNECT')
     OR NOT has_database_privilege('fugates_dev_owner', 'fugates_dev', 'CONNECT') THEN
    RAISE EXCEPTION 'DEV_CONNECT_ROLLBACK_TOUCHED_OTHER_ROLE';
  END IF;
END; $postcheck$;

COMMIT;
