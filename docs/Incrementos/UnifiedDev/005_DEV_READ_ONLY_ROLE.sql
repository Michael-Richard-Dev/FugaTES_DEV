-- R162 - Identidade GET-only exclusiva da API privada de leitura 18762 (ESTRATEGIA B).
--
-- POR QUE B, E NAO REVOKE NA ROLE EXISTENTE:
--   fugates_acm_hml_app e compartilhada por tres servicos (18762 leitura, 18761 readback,
--   18763 comandos). O servico 18763 executa `update fugates.integracao`,
--   `insert into fugates.auditoria_integracao` e `update fugates.projecao_resultado_fluxo_mvp`.
--   Um REVOKE nos privilegios "excedentes" quebraria esse servico. Esta migracao NAO toca a
--   role existente: cria uma identidade nova, sem nenhuma escrita, para a API 18762.
--
-- ACL: espelha EXATAMENTE os SELECT que a role atual ja possui e que a API comprovadamente usa.
-- Nenhum INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES ou TRIGGER e concedido.
--
-- Idempotente: pode ser reaplicada; grants repetidos nao geram erro e a role so e criada uma vez.
-- A senha nunca aparece aqui: entra por :'senha', digitada/gerada no terminal do administrador.

\set ON_ERROR_STOP on

BEGIN;
SET TRANSACTION READ WRITE;
SET LOCAL statement_timeout='15s';
SET LOCAL lock_timeout='5s';
SET LOCAL search_path=pg_catalog;

-- ---------------------------------------------------------------------------
-- 1) Guardas fisicas. Qualquer divergencia aborta antes de qualquer escrita.
-- ---------------------------------------------------------------------------
DO $guarda$
BEGIN
  IF current_database() <> 'fugates_dev'
     OR current_setting('port') <> '5433'
     OR current_setting('cluster_name') <> '18/fugatesacmdev'
     OR pg_is_in_recovery()
     OR current_setting('server_version_num')::integer NOT BETWEEN 180000 AND 189999
     OR session_user <> 'postgres' OR current_user <> 'postgres' THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_TARGET_REJECTED';
  END IF;

  -- A role operacional compartilhada tem que existir e NAO pode ser tocada por este script.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_acm_hml_app' AND rolcanlogin) THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_SHARED_ROLE_MISSING';
  END IF;
END; $guarda$;

-- ---------------------------------------------------------------------------
-- 2) Role GET-only. Idempotente. Sem atributos privilegiados, sem heranca.
-- ---------------------------------------------------------------------------
-- A senha chega por variavel do psql. O SET LOCAL e uma instrucao simples, portanto :'senha'
-- e interpolado aqui; dentro de um bloco dollar-quoted o psql NAO interpola, por isso o valor
-- e lido depois com current_setting. O SET LOCAL morre no COMMIT, sem deixar residuo na sessao.
SET LOCAL fugates.senha_dev_read = :'senha';

DO $criar$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_dev_read_app') THEN
    EXECUTE format(
      'CREATE ROLE fugates_dev_read_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE '
      'NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 8 PASSWORD %L',
      current_setting('fugates.senha_dev_read'));
  END IF;
END; $criar$;

-- Sessao sempre somente leitura, mesmo que um privilegio seja concedido por engano no futuro.
ALTER ROLE fugates_dev_read_app SET default_transaction_read_only = on;
ALTER ROLE fugates_dev_read_app SET search_path = fugates;

-- ---------------------------------------------------------------------------
-- 3) SELECT exatamente igual ao que a API 18762 ja consome hoje. Nada alem disso.
-- ---------------------------------------------------------------------------
SET LOCAL ROLE fugates_dev_owner;

GRANT USAGE ON SCHEMA fugates    TO fugates_dev_read_app;
GRANT USAGE ON SCHEMA masterdata TO fugates_dev_read_app;

GRANT SELECT ON fugates.contrato_sap_toledo_identidade   TO fugates_dev_read_app;
GRANT SELECT ON fugates.contrato_sap_toledo_versao       TO fugates_dev_read_app;
GRANT SELECT ON fugates.correlacao_guardian_evento       TO fugates_dev_read_app;
GRANT SELECT ON fugates.correlacao_guardian_sap          TO fugates_dev_read_app;
GRANT SELECT ON fugates.projecao_resultado_fluxo_mvp     TO fugates_dev_read_app;
GRANT SELECT ON masterdata.carrier_single_create_journal TO fugates_dev_read_app;

GRANT SELECT (escopo, sistema_externo, operacao, ativa)
  ON fugates.pausa_processamento TO fugates_dev_read_app;

GRANT SELECT (id, integracao_id, evento, criada_em, worker, fencing_token, tentativa_id, diagnostico)
  ON fugates.auditoria_integracao TO fugates_dev_read_app;

GRANT SELECT (id, integracao_id, fencing_token, tipo_disparo, fase_envio, desfecho,
              criada_em, envio_iniciado_em, resultado_recebido_em)
  ON fugates.tentativa_integracao TO fugates_dev_read_app;

GRANT SELECT (id, origem, destino, operacao, chave_negocio, fingerprint_sha256, estado,
              tipo_disparo_pendente, proxima_tentativa_em, fencing_token, worker_proprietario,
              lease_expira_em, fase_envio, tentativa_atual_id, correlation_id,
              criado_em, atualizado_em)
  ON fugates.integracao TO fugates_dev_read_app;

RESET ROLE;

-- ---------------------------------------------------------------------------
-- 4) Postcheck.
-- ---------------------------------------------------------------------------
DO $postcheck$
DECLARE faltando text; excedente text;
BEGIN
  -- 4.1 nenhuma escrita, em lugar nenhum dos dois schemas
  SELECT string_agg(format('%s.%s:%s', n.nspname, c.relname, v.acao), ', ')
    INTO excedente
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
   CROSS JOIN (VALUES ('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE')) v(acao)
   WHERE c.relkind IN ('r','p') AND n.nspname IN ('fugates','masterdata')
     AND has_table_privilege('fugates_dev_read_app', c.oid, v.acao);
  IF excedente IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_HAS_WRITE_PRIVILEGE: %', excedente;
  END IF;

  -- 4.2 a leitura que a API precisa esta presente
  SELECT string_agg(v.obj, ', ') INTO faltando
    FROM (VALUES
      ('fugates.contrato_sap_toledo_identidade'),
      ('fugates.contrato_sap_toledo_versao'),
      ('fugates.correlacao_guardian_evento'),
      ('fugates.correlacao_guardian_sap'),
      ('fugates.projecao_resultado_fluxo_mvp'),
      ('masterdata.carrier_single_create_journal')
    ) v(obj)
   WHERE NOT has_table_privilege('fugates_dev_read_app', v.obj, 'SELECT');
  IF faltando IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_SELECT_INCOMPLETE: %', faltando;
  END IF;

  SELECT string_agg(a.attname, ', ') INTO faltando
    FROM unnest(ARRAY['id','origem','destino','operacao','chave_negocio','fingerprint_sha256',
                      'estado','correlation_id','criado_em','atualizado_em']) a(attname)
   WHERE NOT has_column_privilege('fugates_dev_read_app','fugates.integracao',a.attname,'SELECT');
  IF faltando IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_SELECT_INCOMPLETE: integracao(%)', faltando;
  END IF;

  -- 4.3 role nao privilegiada e nao membro do owner
  IF EXISTS (SELECT 1 FROM pg_roles
              WHERE rolname='fugates_dev_read_app'
                AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls OR rolreplication))
     OR pg_has_role('fugates_dev_read_app','fugates_dev_owner','USAGE') THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_OVERPRIVILEGED';
  END IF;

  -- 4.4 a role compartilhada ficou EXATAMENTE como estava (nao e tocada por este script)
  IF NOT has_table_privilege('fugates_acm_hml_app','fugates.integracao','INSERT')
     OR NOT has_table_privilege('fugates_acm_hml_app','fugates.contrato_sap_toledo_versao','INSERT')
     OR NOT has_column_privilege('fugates_acm_hml_app','fugates.integracao','estado','UPDATE') THEN
    RAISE EXCEPTION 'DEV_READ_ROLE_SHARED_ROLE_AFFECTED';
  END IF;
END; $postcheck$;

COMMIT;

-- Rollback: 005_ROLLBACK_DEV_READ_ONLY_ROLE.sql
