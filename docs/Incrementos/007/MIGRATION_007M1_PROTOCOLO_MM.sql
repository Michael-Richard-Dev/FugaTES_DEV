-- CANDIDATE ONLY — NOT EXECUTED. MM isolated registry, PostgreSQL18 DEV5433.
-- Prerequisites: explicit operator approval, backup/restore evidence, no MM writers,
-- existing schema owner and dedicated LOGIN fugates_mm_app provisioned independently.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
DO $$ BEGIN
 IF current_database()<>'fugates_dev' OR current_setting('port')<>'5433'
    OR current_setting('cluster_name')<>'18/fugatesacmdev' THEN
   RAISE EXCEPTION 'MM_WRONG_CLUSTER';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fugates_mm_app' AND rolcanlogin
    AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls) THEN
   RAISE EXCEPTION 'MM_DEDICATED_APPLICATION_ROLE_REQUIRED';
 END IF;
END $$;
SET LOCAL ROLE fugates_dev_owner;
CREATE TABLE fugates.protocolo_mm_pedido_compra(
 reserva_id uuid PRIMARY KEY,
 chave_natural text NOT NULL UNIQUE,
 empresa text NOT NULL CHECK(empresa='3000'),
 pedido text NOT NULL CHECK(pedido ~ '^[1-9][0-9]{0,9}$'),
 item text NOT NULL CHECK(item ~ '^[1-9][0-9]{0,4}$'),
 centro text NOT NULL CHECK(centro IN('3011','3008')),
 payload_json text NOT NULL CHECK(jsonb_typeof(payload_json::jsonb)='object'),
 fonte_sha256 text NOT NULL CHECK(fonte_sha256 ~ '^[0-9A-F]{64}$'),
 payload_sha256 text NOT NULL CHECK(payload_sha256 ~ '^[0-9A-F]{64}$'),
 estado text NOT NULL CHECK(estado IN('INDETERMINADO','CRIADO','RECUSADO')),
 http_status smallint NULL CHECK(http_status BETWEEN 100 AND 599),
 protocolo_id bigint NULL UNIQUE CHECK(protocolo_id>0),
 mensagem_segura text NULL CHECK(length(mensagem_segura)<=400),
 criado_em timestamptz NOT NULL DEFAULT now(),
 atualizado_em timestamptz NOT NULL DEFAULT now(),
 CHECK(chave_natural=empresa||'/'||pedido||'/'||item),
 CHECK(estado<>'CRIADO' OR (protocolo_id IS NOT NULL AND http_status BETWEEN 200 AND 299))
);
REVOKE ALL ON fugates.protocolo_mm_pedido_compra FROM PUBLIC;
GRANT USAGE ON SCHEMA fugates TO fugates_mm_app;
GRANT SELECT,INSERT ON fugates.protocolo_mm_pedido_compra TO fugates_mm_app;
GRANT UPDATE(estado,http_status,protocolo_id,mensagem_segura,atualizado_em)
 ON fugates.protocolo_mm_pedido_compra TO fugates_mm_app;
-- No sequences, functions, default privileges or grants to legacy/application identities.
COMMIT;
