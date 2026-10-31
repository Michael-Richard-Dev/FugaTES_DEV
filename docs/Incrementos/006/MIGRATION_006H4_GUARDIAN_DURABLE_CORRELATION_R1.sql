-- PREPARADA; NAO EXECUTAR NESTE GATE.
BEGIN;
SET LOCAL ROLE fugates_dev_owner;
SET LOCAL search_path TO fugates, pg_catalog;
DO $check$
BEGIN
  IF to_regclass('fugates.contrato_sap_toledo_versao') IS NULL
     OR to_regclass('fugates.integracao') IS NULL THEN
    RAISE EXCEPTION 'H4_GUARDIAN_PRECHECK_BASELINE_MISSING';
  END IF;
  IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='fugates'
       AND table_name='contrato_sap_toledo_versao' AND column_name='guardian_numero_referencia') THEN
    RAISE EXCEPTION 'H4_GUARDIAN_PRECHECK_ALREADY_APPLIED';
  END IF;
END $check$;
ALTER TABLE fugates.contrato_sap_toledo_versao
 ADD COLUMN guardian_numero_referencia text NULL,
 ADD COLUMN guardian_codigo_solicitado text NULL,
 ADD COLUMN guardian_codigo_confirmado text NULL,
 ADD COLUMN guardian_protocolo_id bigint NULL,
 ADD COLUMN guardian_numero_ticket text NULL,
 ADD COLUMN guardian_callback_id bigint NULL,
 ADD COLUMN guardian_callback_sequencial bigint NULL,
 ADD COLUMN guardian_callback_fingerprint text NULL;
ALTER TABLE fugates.contrato_sap_toledo_versao
 ADD CONSTRAINT ck_h4_guardian_reserva_completa CHECK
  ((guardian_numero_referencia IS NULL AND guardian_codigo_solicitado IS NULL)
   OR (guardian_numero_referencia IS NOT NULL AND length(btrim(guardian_numero_referencia)) > 0
       AND guardian_codigo_solicitado IS NOT NULL AND length(btrim(guardian_codigo_solicitado)) > 0)),
 ADD CONSTRAINT ck_h4_guardian_confirmacao CHECK
  (guardian_codigo_confirmado IS NULL OR
   (guardian_codigo_confirmado = guardian_codigo_solicitado AND guardian_numero_referencia IS NOT NULL)),
 ADD CONSTRAINT ck_h4_guardian_protocolo CHECK
  ((guardian_protocolo_id IS NULL AND guardian_numero_ticket IS NULL)
   OR (guardian_protocolo_id > 0 AND guardian_numero_ticket IS NOT NULL
       AND length(btrim(guardian_numero_ticket)) > 0 AND guardian_codigo_confirmado IS NOT NULL)),
 ADD CONSTRAINT ck_h4_guardian_callback_par CHECK
  ((guardian_callback_id IS NULL AND guardian_callback_sequencial IS NULL AND guardian_callback_fingerprint IS NULL)
   OR (guardian_callback_id > 0 AND guardian_callback_sequencial > 0
       AND guardian_protocolo_id IS NOT NULL)),
 ADD CONSTRAINT ck_h4_guardian_callback_fingerprint CHECK
  (guardian_callback_fingerprint IS NULL OR
   (length(guardian_callback_fingerprint)=64 AND guardian_callback_fingerprint ~ '^[0-9A-F]{64}$'));
CREATE UNIQUE INDEX ux_h4_guardian_protocolo_id ON fugates.contrato_sap_toledo_versao(guardian_protocolo_id)
 WHERE guardian_protocolo_id IS NOT NULL;
CREATE UNIQUE INDEX ux_h4_guardian_callback_identity ON fugates.contrato_sap_toledo_versao(guardian_callback_id,guardian_callback_sequencial)
 WHERE guardian_callback_id IS NOT NULL;
CREATE INDEX ix_h4_guardian_referencia ON fugates.contrato_sap_toledo_versao(guardian_numero_referencia)
 WHERE guardian_numero_referencia IS NOT NULL;
-- Permissoes da tabela H4 original ja compreendem UPDATE/SELECT para a role da aplicacao.
COMMIT;
