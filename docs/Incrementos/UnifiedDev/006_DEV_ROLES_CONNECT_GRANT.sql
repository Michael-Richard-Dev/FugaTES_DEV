-- R166 - CONNECT ON DATABASE para as duas identidades DEV criadas por 005 e 004.
--
-- POR QUE ESTE DELTA EXISTE:
--   005 e 004 concedem USAGE nos schemas e privilegios de tabela/coluna, mas NENHUM dos dois
--   concede CONNECT ON DATABASE. Nesta base o CONNECT do PUBLIC esta revogado, portanto cada
--   role precisa do privilegio explicitamente. Comprovado no servidor, somente leitura:
--
--     has_database_privilege(..., 'fugates_dev', 'CONNECT')
--       fugates_acm_hml_app  = true
--       fugates_dev_app      = true
--       fugates_dev_owner    = true
--       fugates_dev_read_app = false   <-- criada por 005
--       fugates_sap_inbound_app = false <-- criada por 004
--
--   E o erro real, obtido com psql -w (que nunca pede senha):
--     FATAL: permission denied for database "fugates_dev"
--     DETAIL: User does not have CONNECT privilege.
--
--   A autenticacao por senha das duas roles FUNCIONA: a falha e de autorizacao, depois do
--   handshake. Sem este delta, a role de leitura nao serve a API 18762 e a role de escrita
--   falharia no recebimento SAP real, no momento da ativacao.
--
-- ESCOPO: exclusivamente CONNECT, exclusivamente para as duas roles novas. Nenhum CREATE,
-- nenhum TEMP, nada para PUBLIC, e a role compartilhada fugates_acm_hml_app nao e tocada.
--
-- Idempotente: pode ser reaplicada; GRANT repetido nao gera erro.

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
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_TARGET_REJECTED';
  END IF;

  -- As duas roles tem que existir e poder logar: este delta nao cria identidade.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_dev_read_app' AND rolcanlogin) THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_READ_ROLE_MISSING';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_sap_inbound_app' AND rolcanlogin) THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_WRITER_ROLE_MISSING';
  END IF;

  -- A role operacional compartilhada tem que existir e NAO pode ser tocada por este script.
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_acm_hml_app' AND rolcanlogin) THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_SHARED_ROLE_MISSING';
  END IF;

  -- Nenhuma das duas pode ter virado privilegiada ou membro do owner entre 005/004 e agora.
  IF EXISTS (SELECT 1 FROM pg_roles
              WHERE rolname IN ('fugates_dev_read_app','fugates_sap_inbound_app')
                AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls)) THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_ROLE_IS_PRIVILEGED';
  END IF;
END; $guarda$;

-- ---------------------------------------------------------------------------
-- 2) O unico privilegio deste delta. Nada para PUBLIC.
-- ---------------------------------------------------------------------------
GRANT CONNECT ON DATABASE fugates_dev TO fugates_dev_read_app;
GRANT CONNECT ON DATABASE fugates_dev TO fugates_sap_inbound_app;

-- ---------------------------------------------------------------------------
-- 3) Postcheck: completude, ausencia de excesso e preservacao do que ja existia.
-- ---------------------------------------------------------------------------
DO $postcheck$
DECLARE faltando text; excedente text;
BEGIN
  -- 3.1 as duas roles conectam
  SELECT string_agg(v.role, ', ') INTO faltando
    FROM (VALUES ('fugates_dev_read_app'),('fugates_sap_inbound_app')) v(role)
   WHERE NOT has_database_privilege(v.role, 'fugates_dev', 'CONNECT');
  IF faltando IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_INCOMPLETE: %', faltando;
  END IF;

  -- 3.2 e nao ganharam mais nada no nivel de banco
  SELECT string_agg(format('%s:%s', v.role, p.priv), ', ') INTO excedente
    FROM (VALUES ('fugates_dev_read_app'),('fugates_sap_inbound_app')) v(role)
   CROSS JOIN (VALUES ('CREATE'),('TEMPORARY')) p(priv)
   WHERE has_database_privilege(v.role, 'fugates_dev', p.priv);
  IF excedente IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_EXCESS_DATABASE_PRIVILEGE: %', excedente;
  END IF;

  -- 3.3 PUBLIC continua sem CONNECT: o delta nao reabre a base para todos
  IF has_database_privilege('public', 'fugates_dev', 'CONNECT') THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_PUBLIC_WOULD_CONNECT';
  END IF;

  -- 3.4 a role compartilhada segue conectando, exatamente como antes
  IF NOT has_database_privilege('fugates_acm_hml_app', 'fugates_dev', 'CONNECT') THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_SHARED_ROLE_BROKEN';
  END IF;

  -- 3.5 a role de leitura continua sem qualquer escrita: este delta nao mexeu em tabela
  SELECT string_agg(format('%s.%s:%s', n.nspname, c.relname, v.acao), ', ')
    INTO excedente
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
   CROSS JOIN (VALUES ('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE')) v(acao)
   WHERE c.relkind IN ('r','p') AND n.nspname IN ('fugates','masterdata')
     AND has_table_privilege('fugates_dev_read_app', c.oid, v.acao);
  IF excedente IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_CONNECT_GRANT_READ_ROLE_HAS_WRITE: %', excedente;
  END IF;
END; $postcheck$;

COMMIT;
