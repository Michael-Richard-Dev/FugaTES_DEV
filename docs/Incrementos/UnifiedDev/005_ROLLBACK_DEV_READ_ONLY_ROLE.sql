-- R162 - Rollback da identidade GET-only da API 18762.
--
-- Revoga apenas o que 005_DEV_READ_ONLY_ROLE.sql concedeu e remove a role.
-- NAO toca fugates_acm_hml_app, fugates_sap_inbound_app, fugates_dev_app nem o owner.
-- NAO remove tabelas, dados ou privilegios de terceiros.
--
-- ATENCAO OPERACIONAL: executar somente depois de a API 18762 voltar a apontar para a
-- identidade anterior; caso contrario o servico de leitura fica sem credencial valida.

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
    RAISE EXCEPTION 'DEV_READ_ROLE_ROLLBACK_TARGET_REJECTED';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_dev_read_app') THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_ROLLBACK_ROLE_ABSENT';
  END IF;
END; $guarda$;

SET LOCAL ROLE fugates_dev_owner;

REVOKE SELECT ON fugates.contrato_sap_toledo_identidade   FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.contrato_sap_toledo_versao       FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.correlacao_guardian_evento       FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.correlacao_guardian_sap          FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.projecao_resultado_fluxo_mvp     FROM fugates_dev_read_app;
REVOKE SELECT ON masterdata.carrier_single_create_journal FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.pausa_processamento              FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.auditoria_integracao             FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.tentativa_integracao             FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.integracao                       FROM fugates_dev_read_app;
REVOKE USAGE  ON SCHEMA fugates                           FROM fugates_dev_read_app;
REVOKE USAGE  ON SCHEMA masterdata                        FROM fugates_dev_read_app;

RESET ROLE;

ALTER ROLE fugates_dev_read_app RESET default_transaction_read_only;
ALTER ROLE fugates_dev_read_app RESET search_path;
DROP ROLE fugates_dev_read_app;

DO $postcheck$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_dev_read_app') THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_ROLLBACK_INCOMPLETE';
  END IF;
  -- A role compartilhada tem que continuar exatamente como estava.
  IF NOT has_table_privilege('fugates_acm_hml_app','fugates.integracao','INSERT')
     OR NOT has_column_privilege('fugates_acm_hml_app','fugates.integracao','estado','UPDATE') THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_ROLLBACK_SHARED_ROLE_AFFECTED';
  END IF;
END; $postcheck$;

COMMIT;
