-- REVIEW ONLY / NOT EXECUTED. Apply original PROPOSED_NOT_EXECUTED.sql under the approved owner FIRST.
-- No parent data rewrite, existing correlation update, worker enqueue, role creation or GRANT.
BEGIN;
SET LOCAL lock_timeout='1s';
SET LOCAL statement_timeout='30s';
DO $guard$
BEGIN
 IF current_setting('cluster_name') IS DISTINCT FROM '18/fugatesacmdev'
 OR current_setting('port') IS DISTINCT FROM '5433' OR current_database() IS DISTINCT FROM 'fugates_dev'
 OR pg_is_in_recovery() OR current_setting('server_version_num')::int NOT BETWEEN 180000 AND 189999
 OR session_user IS DISTINCT FROM 'postgres'
 OR to_regclass('fugates.toledo_pesagem_recebida') IS NULL THEN
  RAISE EXCEPTION 'TOLEDO_V2_TARGET_OR_V1_PRECONDITION_FAILED';
 END IF;
 IF (SELECT relowner::regrole::text FROM pg_class WHERE oid='fugates.toledo_pesagem_recebida'::regclass)
 IS DISTINCT FROM 'fugates_dev_owner' THEN RAISE EXCEPTION 'TOLEDO_V2_PARENT_OWNER_MISMATCH'; END IF;
END $guard$;
SET LOCAL ROLE fugates_dev_owner;
ALTER TABLE fugates.toledo_pesagem_recebida ADD COLUMN origem_autenticada text
 CHECK(origem_autenticada IS NULL OR length(origem_autenticada) BETWEEN 1 AND 160);
-- Nullable only to preserve an existing V1 receipt's unproven historical source; new code requires a source.
CREATE TABLE fugates.toledo_pesagem_item (
 recibo_id uuid NOT NULL REFERENCES fugates.toledo_pesagem_recebida(recibo_id),
 indice integer NOT NULL CHECK(indice>=0), nr_indice_total integer NOT NULL CHECK(nr_indice_total BETWEEN 1 AND 1000),
 contrato_acm text NOT NULL CHECK(contrato_acm ~ '^[0-9]{1,128}$'),
 id_parceiro text NOT NULL CHECK(id_parceiro ~ '^[0-9]{1,128}$'),
 documento_item jsonb NOT NULL CHECK(jsonb_typeof(documento_item)='object'),
 integracao_id uuid NULL REFERENCES fugates.integracao(id), versao_h4 bigint NULL,
 estado_correlacao text NOT NULL CHECK(estado_correlacao IN ('H4_MATCHED','H4_NOT_FOUND','H4_AMBIGUOUS','H4_UNVERIFIED')),
 PRIMARY KEY(recibo_id,indice),
 FOREIGN KEY(contrato_acm,versao_h4) REFERENCES fugates.contrato_sap_toledo_versao(numero_contrato,versao),
 CHECK((estado_correlacao='H4_MATCHED' AND integracao_id IS NOT NULL AND versao_h4 IS NOT NULL AND versao_h4>0)
    OR (estado_correlacao<>'H4_MATCHED' AND integracao_id IS NULL AND versao_h4 IS NULL))
);
CREATE INDEX ix_toledo_pesagem_item_integracao ON fugates.toledo_pesagem_item(integracao_id) WHERE integracao_id IS NOT NULL;
CREATE TABLE fugates.toledo_pesagem_entrega (
 entrega_id uuid PRIMARY KEY,recibo_id uuid NOT NULL REFERENCES fugates.toledo_pesagem_recebida(recibo_id),
 origem_autenticada text NOT NULL CHECK(length(origem_autenticada) BETWEEN 1 AND 160),
 recebido_em timestamptz NOT NULL,resultado text NOT NULL CHECK(resultado IN ('NEW_RECEIPT','IDEMPOTENT'))
);
COMMIT;
