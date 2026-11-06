-- CANDIDATE ONLY / NOT EXECUTED. Additive MM retry journal, DEV5433 only.
-- Requires operational approval, consistent backup, drained MM + command writers and existing007M1.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
DO $$ BEGIN
 IF current_database()<>'fugates_dev' OR current_setting('port')<>'5433' OR current_setting('cluster_name')<>'18/fugatesacmdev' THEN RAISE EXCEPTION 'MM_WRONG_CLUSTER';END IF;
 IF to_regclass('fugates.protocolo_mm_pedido_compra') IS NULL THEN RAISE EXCEPTION 'MM_REGISTRY_REQUIRED';END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fugates_mm_app' AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls) THEN RAISE EXCEPTION 'MM_DEDICATED_ROLE_REQUIRED';END IF;
END $$;
SET LOCAL ROLE fugates_dev_owner;
CREATE TABLE fugates.protocolo_mm_reenvio_comando(
 comando_id uuid PRIMARY KEY,
 reserva_id uuid NOT NULL REFERENCES fugates.protocolo_mm_pedido_compra(reserva_id),
 chave_natural text NOT NULL CHECK(chave_natural ~ '^3000/[1-9][0-9]{0,9}/[1-9][0-9]{0,4}$'),
 anterior_json jsonb NOT NULL CHECK(jsonb_typeof(anterior_json)='object'),
 snapshot_sha256 text NOT NULL CHECK(snapshot_sha256 ~ '^[0-9A-F]{64}$'),
 operador_sid text NOT NULL CHECK(operador_sid ~ '^S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+$'),
 justificativa text NOT NULL CHECK(length(justificativa) BETWEEN 8 AND 500),
 estado text NOT NULL CHECK(estado IN('PENDENTE','EM_PROCESSAMENTO','CONCLUIDO','BLOQUEADO','INDETERMINADO')),
 claim_token uuid NULL,
 envio_iniciado_em timestamptz NULL,
 resultado_json jsonb NULL CHECK(resultado_json IS NULL OR jsonb_typeof(resultado_json)='object'),
 criado_em timestamptz NOT NULL DEFAULT now(),
 atualizado_em timestamptz NOT NULL DEFAULT now(),
 CHECK(estado='PENDENTE' OR claim_token IS NOT NULL),
 CHECK(estado NOT IN('CONCLUIDO','BLOQUEADO','INDETERMINADO') OR resultado_json IS NOT NULL),
 CHECK(estado<>'CONCLUIDO' OR envio_iniciado_em IS NOT NULL)
);
CREATE UNIQUE INDEX ux_mm_reenvio_em_aberto ON fugates.protocolo_mm_reenvio_comando(chave_natural) WHERE estado IN('PENDENTE','EM_PROCESSAMENTO','INDETERMINADO');
REVOKE ALL ON fugates.protocolo_mm_reenvio_comando FROM PUBLIC;
-- Neutralize inherited default grants ONLY on this newly-created table, never schema/global ACLs.
DO $$ DECLARE target_role text; BEGIN
 FOR target_role IN SELECT DISTINCT r.rolname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
  CROSS JOIN LATERAL aclexplode(c.relacl) a JOIN pg_roles r ON r.oid=a.grantee
  WHERE n.nspname='fugates' AND c.relname='protocolo_mm_reenvio_comando' AND a.grantee<>c.relowner
 LOOP EXECUTE format('REVOKE ALL ON fugates.protocolo_mm_reenvio_comando FROM %I',target_role);END LOOP;
END $$;
GRANT SELECT,INSERT ON fugates.protocolo_mm_reenvio_comando TO fugates_mm_app;
GRANT UPDATE(estado,claim_token,envio_iniciado_em,resultado_json,atualizado_em) ON fugates.protocolo_mm_reenvio_comando TO fugates_mm_app;
-- No grants to old/ACM/GET-only identities. No default ACL changes, sequences, custom functions or original table DDL.
COMMIT;
