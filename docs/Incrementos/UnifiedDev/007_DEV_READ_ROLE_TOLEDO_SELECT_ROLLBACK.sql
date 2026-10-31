-- R166 - ROLLBACK do 007: retira da identidade GET-only a leitura das tabelas Toledo.
--
-- Efeito: /dev/read/toledo volta a 503 UNIFIED_DEV_READ_UNAVAILABLE enquanto a API 18762 estiver
-- servindo como fugates_dev_read_app. Os demais recursos (identity, contracts, masterdata,
-- monitor, protocol, detail) nao sao afetados.
--
-- NAO toca fugates_acm_hml_app, fugates_sap_inbound_app nem PUBLIC, e devolve integracao ao
-- conjunto de colunas do 005.

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
    RAISE EXCEPTION 'DEV_READ_TOLEDO_ROLLBACK_TARGET_REJECTED';
  END IF;
END; $guarda$;

REVOKE SELECT ON fugates.toledo_pesagem_recebida FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.toledo_pesagem_item     FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.tentativa_integracao    FROM fugates_dev_read_app;
REVOKE SELECT ON fugates.auditoria_integracao    FROM fugates_dev_read_app;
REVOKE SELECT (worker_proprietario, lease_expira_em) ON fugates.integracao FROM fugates_dev_read_app;

DO $postcheck$
DECLARE restante text;
BEGIN
  SELECT string_agg(v.obj, ', ') INTO restante
    FROM (VALUES
      ('fugates.toledo_pesagem_recebida'),
      ('fugates.toledo_pesagem_item'),
      ('fugates.tentativa_integracao'),
      ('fugates.auditoria_integracao')
    ) v(obj)
   WHERE has_table_privilege('fugates_dev_read_app', v.obj, 'SELECT');
  IF restante IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_ROLLBACK_INCOMPLETE: %', restante;
  END IF;

  -- O que o 005 concedeu tem que continuar de pe: o rollback e do 007, nao do 005.
  SELECT string_agg(v.obj, ', ') INTO restante
    FROM (VALUES
      ('fugates.contrato_sap_toledo_identidade'),
      ('fugates.contrato_sap_toledo_versao'),
      ('fugates.correlacao_guardian_evento'),
      ('fugates.correlacao_guardian_sap'),
      ('fugates.projecao_resultado_fluxo_mvp')
    ) v(obj)
   WHERE NOT has_table_privilege('fugates_dev_read_app', v.obj, 'SELECT');
  IF restante IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_ROLLBACK_BROKE_005: %', restante;
  END IF;

  IF NOT has_column_privilege('fugates_dev_read_app','fugates.integracao','estado','SELECT') THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_ROLLBACK_BROKE_005: integracao(estado)';
  END IF;

  IF NOT has_table_privilege('fugates_acm_hml_app','fugates.integracao','INSERT') THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_ROLLBACK_TOUCHED_SHARED_ROLE';
  END IF;
END; $postcheck$;

COMMIT;
