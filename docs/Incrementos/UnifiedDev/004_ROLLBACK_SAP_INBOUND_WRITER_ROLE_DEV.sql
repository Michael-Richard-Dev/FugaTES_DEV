-- R161 - Rollback da identidade exclusiva de escrita do receptor SAP (DEV 5433).
--
-- Revoga apenas o que 004_SAP_INBOUND_WRITER_ROLE_DEV.sql concedeu e remove a role.
-- NAO toca a role leitora fugates_acm_hml_app, NAO toca fugates_dev_app nem o owner,
-- NAO remove tabelas, dados ou privilegios de terceiros.

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
     OR session_user <> 'postgres' THEN
    RAISE EXCEPTION 'SAP_WRITER_ROLLBACK_TARGET_REJECTED';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_sap_inbound_app') THEN
    RAISE EXCEPTION 'SAP_WRITER_ROLLBACK_ROLE_ABSENT';
  END IF;
END; $guarda$;

SET LOCAL ROLE fugates_dev_owner;

REVOKE UPDATE (ultima_versao) ON fugates.contrato_sap_toledo_identidade FROM fugates_sap_inbound_app;
REVOKE SELECT, INSERT ON fugates.contrato_sap_toledo_identidade FROM fugates_sap_inbound_app;
REVOKE SELECT, INSERT ON fugates.contrato_sap_toledo_versao FROM fugates_sap_inbound_app;
REVOKE SELECT (id, correlation_id, criado_em, origem, destino,
               operacao, chave_negocio, fingerprint_sha256)
  ON fugates.integracao FROM fugates_sap_inbound_app;
REVOKE INSERT ON fugates.integracao FROM fugates_sap_inbound_app;
REVOKE USAGE ON SCHEMA fugates FROM fugates_sap_inbound_app;

RESET ROLE;

DROP ROLE fugates_sap_inbound_app;

DO $postcheck$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_sap_inbound_app') THEN
    RAISE EXCEPTION 'SAP_WRITER_ROLLBACK_INCOMPLETE';
  END IF;
  -- A leitora tem que permanecer exatamente como estava.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_acm_hml_app' AND rolcanlogin) THEN
    RAISE EXCEPTION 'SAP_WRITER_ROLLBACK_READER_AFFECTED';
  END IF;
END; $postcheck$;

COMMIT;
