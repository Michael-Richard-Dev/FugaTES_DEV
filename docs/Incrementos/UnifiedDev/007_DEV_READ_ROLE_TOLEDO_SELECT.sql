-- R166 - SELECT das tabelas de recebimento Toledo para a identidade GET-only da API 18762.
--
-- POR QUE ESTE DELTA EXISTE:
--   O 005 concedeu SELECT numa lista FIXA de tabelas, definida antes de existirem as tabelas de
--   recebimento Toledo. Ao promover a API 18762 de fugates_acm_hml_app (leitura ampla) para
--   fugates_dev_read_app (lista fixa), o recurso /dev/read/toledo passou a devolver
--   503 UNIFIED_DEV_READ_UNAVAILABLE, porque a consulta precisa de tabelas fora dessa lista.
--
--   Estado observado no servidor, somente leitura:
--     fugates.toledo_pesagem_recebida = false
--     fugates.toledo_pesagem_item     = false
--     fugates.tentativa_integracao    = false
--     fugates.auditoria_integracao    = false
--
-- ESCOPO: exclusivamente SELECT, exclusivamente nas tabelas e colunas que a consulta do monitor
-- Toledo realmente usa (RecebimentosToledoPostgreSqlReadRepository.SqlLista e SqlJson).
-- Nenhuma escrita, nada para PUBLIC, nada para a role de escrita do receptor SAP, e a role
-- compartilhada fugates_acm_hml_app nao e tocada.
--
-- Em integracao o 005 ja concedeu 10 colunas. A consulta Toledo usa duas alem dessas
-- (worker_proprietario e lease_expira_em, para derivar o flag "ativo"), concedidas aqui por COLUNA,
-- preservando a restricao por coluna da tabela: SELECT de tabela inteira continua NAO concedido.
--
-- Idempotente: pode ser reaplicada; GRANT repetido nao gera erro.

\set ON_ERROR_STOP on

BEGIN;
SET TRANSACTION READ WRITE;
SET LOCAL statement_timeout='15s';
SET LOCAL lock_timeout='5s';
SET LOCAL search_path=pg_catalog;

-- ---------------------------------------------------------------------------
-- 1) Guardas fisicas.
-- ---------------------------------------------------------------------------
DO $guarda$
BEGIN
  IF current_database() <> 'fugates_dev'
     OR current_setting('port') <> '5433'
     OR current_setting('cluster_name') <> '18/fugatesacmdev'
     OR pg_is_in_recovery()
     OR session_user <> 'postgres' OR current_user <> 'postgres' THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_TARGET_REJECTED';
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_dev_read_app' AND rolcanlogin) THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_ROLE_MISSING';
  END IF;

  -- A role nao pode ter virado privilegiada entre o 005 e agora.
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_dev_read_app'
               AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls)) THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_ROLE_IS_PRIVILEGED';
  END IF;

  -- As tabelas alvo tem que existir: este delta nao cria objeto nenhum.
  IF (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
       WHERE n.nspname='fugates' AND c.relkind IN ('r','p')
         AND c.relname IN ('toledo_pesagem_recebida','toledo_pesagem_item',
                           'tentativa_integracao','auditoria_integracao')) <> 4 THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_TABLES_MISSING';
  END IF;
END; $guarda$;

-- ---------------------------------------------------------------------------
-- 2) Os unicos privilegios deste delta. Somente SELECT.
-- ---------------------------------------------------------------------------
GRANT SELECT ON fugates.toledo_pesagem_recebida TO fugates_dev_read_app;
GRANT SELECT ON fugates.toledo_pesagem_item     TO fugates_dev_read_app;
GRANT SELECT ON fugates.tentativa_integracao    TO fugates_dev_read_app;
GRANT SELECT ON fugates.auditoria_integracao    TO fugates_dev_read_app;

-- integracao permanece restrita por COLUNA; apenas as duas que faltavam sao acrescentadas.
GRANT SELECT (worker_proprietario, lease_expira_em) ON fugates.integracao TO fugates_dev_read_app;

-- ---------------------------------------------------------------------------
-- 3) Postcheck: completude, ausencia de escrita e preservacao do que ja existia.
-- ---------------------------------------------------------------------------
DO $postcheck$
DECLARE faltando text; excedente text;
BEGIN
  -- 3.1 a leitura que o monitor Toledo precisa esta presente
  SELECT string_agg(v.obj, ', ') INTO faltando
    FROM (VALUES
      ('fugates.toledo_pesagem_recebida'),
      ('fugates.toledo_pesagem_item'),
      ('fugates.tentativa_integracao'),
      ('fugates.auditoria_integracao')
    ) v(obj)
   WHERE NOT has_table_privilege('fugates_dev_read_app', v.obj, 'SELECT');
  IF faltando IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_SELECT_INCOMPLETE: %', faltando;
  END IF;

  SELECT string_agg(a.attname, ', ') INTO faltando
    FROM unnest(ARRAY['worker_proprietario','lease_expira_em']) a(attname)
   WHERE NOT has_column_privilege('fugates_dev_read_app','fugates.integracao',a.attname,'SELECT');
  IF faltando IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_SELECT_INCOMPLETE: integracao(%)', faltando;
  END IF;

  -- 3.2 NENHUMA escrita apareceu em lugar nenhum dos dois schemas
  SELECT string_agg(format('%s.%s:%s', n.nspname, c.relname, v.acao), ', ')
    INTO excedente
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
   CROSS JOIN (VALUES ('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE')) v(acao)
   WHERE c.relkind IN ('r','p') AND n.nspname IN ('fugates','masterdata')
     AND has_table_privilege('fugates_dev_read_app', c.oid, v.acao);
  IF excedente IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_HAS_WRITE_PRIVILEGE: %', excedente;
  END IF;

  -- 3.3 integracao continua SEM SELECT de tabela inteira: a restricao por coluna sobreviveu
  IF has_table_privilege('fugates_dev_read_app','fugates.integracao','SELECT') THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_INTEGRACAO_WOULD_BE_FULLY_READABLE';
  END IF;

  -- 3.4 a identidade de ESCRITA do receptor SAP nao ganhou leitura Toledo por engano
  SELECT string_agg(v.obj, ', ') INTO excedente
    FROM (VALUES ('fugates.toledo_pesagem_recebida'),('fugates.toledo_pesagem_item')) v(obj)
   WHERE has_table_privilege('fugates_sap_inbound_app', v.obj, 'SELECT');
  IF excedente IS NOT NULL THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_WRITER_ROLE_AFFECTED: %', excedente;
  END IF;

  -- 3.5 a role compartilhada segue intacta
  IF NOT has_table_privilege('fugates_acm_hml_app','fugates.integracao','INSERT') THEN
    RAISE EXCEPTION 'DEV_READ_TOLEDO_SHARED_ROLE_BROKEN';
  END IF;
END; $postcheck$;

COMMIT;
