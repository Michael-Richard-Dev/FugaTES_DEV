-- R169 - Completa os privilegios da identidade de ESCRITA do receptor SAP no DEV unificado.
--
-- POR QUE
--   O 004 concedeu um conjunto apertado, desenhado a partir do codigo atual. O release que esta
--   REALMENTE em execucao no receptor (R159B) precisa de mais, e o POST de teste devolveu HTTP 500.
--   Comprovado no servidor, conectando como a propria role:
--
--     select ... estado ... from fugates.integracao   -> permission denied for table integracao
--     select count(*) from fugates.pausa_processamento -> permission denied
--     select count(*) from fugates.tentativa_integracao -> permission denied
--
--   Colunas de integracao que a role ja lia: chave_negocio, correlation_id, criado_em, destino,
--   fingerprint_sha256, id, operacao, origem. Faltava 'estado', entre outras.
--
-- ESCOPO: somente o necessario para RECEBER e registrar o candidato H4.
--   - SELECT na tabela integracao (substitui a lista por coluna, que se mostrou incompleta)
--   - SELECT em pausa_processamento (o receptor confere pausa antes de gravar)
--   - SELECT, INSERT em tentativa_integracao (registro da tentativa)
--   NENHUM UPDATE novo, NENHUM DELETE, nada em outros schemas, nada para PUBLIC, e a role
--   compartilhada fugates_acm_hml_app nao e tocada.
--
-- Idempotente.

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
    RAISE EXCEPTION 'SAP_WRITER_COMPLETO_TARGET_REJECTED';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_sap_inbound_app' AND rolcanlogin) THEN
    RAISE EXCEPTION 'SAP_WRITER_COMPLETO_ROLE_MISSING';
  END IF;
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_sap_inbound_app'
               AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls)) THEN
    RAISE EXCEPTION 'SAP_WRITER_COMPLETO_ROLE_IS_PRIVILEGED';
  END IF;
END; $guarda$;

GRANT SELECT          ON fugates.integracao          TO fugates_sap_inbound_app;
GRANT SELECT          ON fugates.pausa_processamento TO fugates_sap_inbound_app;
GRANT SELECT, INSERT  ON fugates.tentativa_integracao TO fugates_sap_inbound_app;

DO $postcheck$
DECLARE faltando text; excedente text;
BEGIN
  SELECT string_agg(v.obj, ', ') INTO faltando
    FROM (VALUES ('fugates.integracao'),('fugates.pausa_processamento'),('fugates.tentativa_integracao')) v(obj)
   WHERE NOT has_table_privilege('fugates_sap_inbound_app', v.obj, 'SELECT');
  IF faltando IS NOT NULL THEN
    RAISE EXCEPTION 'SAP_WRITER_COMPLETO_SELECT_INCOMPLETE: %', faltando;
  END IF;

  IF NOT has_table_privilege('fugates_sap_inbound_app','fugates.tentativa_integracao','INSERT') THEN
    RAISE EXCEPTION 'SAP_WRITER_COMPLETO_INSERT_INCOMPLETE: tentativa_integracao';
  END IF;

  -- Nenhum DELETE, em lugar nenhum.
  SELECT string_agg(format('%s.%s', n.nspname, c.relname), ', ') INTO excedente
    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   WHERE c.relkind IN ('r','p') AND n.nspname IN ('fugates','masterdata')
     AND has_table_privilege('fugates_sap_inbound_app', c.oid, 'DELETE');
  IF excedente IS NOT NULL THEN
    RAISE EXCEPTION 'SAP_WRITER_COMPLETO_HAS_DELETE: %', excedente;
  END IF;

  -- O estado da integracao continua NAO atualizavel por esta role: quem muda estado e o motor.
  IF has_column_privilege('fugates_sap_inbound_app','fugates.integracao','estado','UPDATE') THEN
    RAISE EXCEPTION 'SAP_WRITER_COMPLETO_WOULD_UPDATE_ESTADO';
  END IF;

  -- A role compartilhada segue intacta.
  IF NOT has_table_privilege('fugates_acm_hml_app','fugates.integracao','INSERT') THEN
    RAISE EXCEPTION 'SAP_WRITER_COMPLETO_SHARED_ROLE_BROKEN';
  END IF;
END; $postcheck$;

COMMIT;
