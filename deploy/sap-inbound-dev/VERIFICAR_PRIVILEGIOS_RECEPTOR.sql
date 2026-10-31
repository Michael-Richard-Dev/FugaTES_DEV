-- R159 — Verificacao SOMENTE LEITURA dos privilegios exigidos pelo receptor SAP no DEV 5433.
--
-- NAO concede nada. NAO altera nada. NAO escreve linha de negocio.
-- Apenas consulta o catalogo com has_table_privilege e devolve PASS/BLOCKED por tabela.
--
-- Executar como qualquer role com acesso de leitura ao catalogo, no destino aprovado:
--   psql "host=127.0.0.1 port=5433 dbname=fugates_dev" -f VERIFICAR_PRIVILEGIOS_RECEPTOR.sql
--
-- Tabelas escritas pelo receptor, dentro de UMA transacao
-- (ContratoSapToledoVersaoStorePostgreSql.RegistrarAsync):
--   fugates.contrato_sap_toledo_identidade  INSERT, UPDATE, SELECT ... FOR UPDATE
--   fugates.contrato_sap_toledo_versao      INSERT, SELECT
--   fugates.integracao                      INSERT   (H4, via RegistrarCandidatoNaTransacaoAsync)

BEGIN;
SET TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL search_path = pg_catalog;

-- 1) Identidade fisica do destino. Qualquer divergencia invalida a verificacao.
SELECT current_database()                      AS database,
       current_setting('port')                 AS port,
       current_setting('cluster_name', true)   AS cluster,
       current_user                            AS verificador,
       pg_is_in_recovery()                     AS em_recovery,
       (current_database() = 'fugates_dev'
        AND current_setting('port') = '5433'
        AND current_setting('cluster_name', true) = '18/fugatesacmdev'
        AND NOT pg_is_in_recovery())           AS destino_aprovado;

-- 2) A role do receptor existe e NAO e privilegiada.
SELECT rolname,
       rolcanlogin,
       rolsuper,
       rolcreatedb,
       rolcreaterole,
       rolbypassrls,
       pg_has_role('fugates_acm_hml_app', 'fugates_dev_owner', 'USAGE') AS membro_do_owner
  FROM pg_roles
 WHERE rolname = 'fugates_acm_hml_app';

-- 3) Privilegios efetivos por tabela e acao. Esta e a resposta do gate.
SELECT v.tabela,
       v.acao,
       has_table_privilege('fugates_acm_hml_app', v.tabela, v.acao) AS concedido
  FROM (VALUES
        ('fugates.contrato_sap_toledo_identidade', 'SELECT'),
        ('fugates.contrato_sap_toledo_identidade', 'INSERT'),
        ('fugates.contrato_sap_toledo_identidade', 'UPDATE'),
        ('fugates.contrato_sap_toledo_versao',     'SELECT'),
        ('fugates.contrato_sap_toledo_versao',     'INSERT'),
        ('fugates.integracao',                     'SELECT'),
        ('fugates.integracao',                     'INSERT')
       ) AS v(tabela, acao)
 ORDER BY v.tabela, v.acao;

-- 4) Veredito unico, para registro na devolutiva.
SELECT CASE WHEN bool_and(has_table_privilege('fugates_acm_hml_app', v.tabela, v.acao))
            THEN 'INBOUND_DATABASE_ACL=PASS'
            ELSE 'INBOUND_DATABASE_ACL=BLOCKED'
       END AS veredito
  FROM (VALUES
        ('fugates.contrato_sap_toledo_identidade', 'SELECT'),
        ('fugates.contrato_sap_toledo_identidade', 'INSERT'),
        ('fugates.contrato_sap_toledo_identidade', 'UPDATE'),
        ('fugates.contrato_sap_toledo_versao',     'SELECT'),
        ('fugates.contrato_sap_toledo_versao',     'INSERT'),
        ('fugates.integracao',                     'SELECT'),
        ('fugates.integracao',                     'INSERT')
       ) AS v(tabela, acao);

-- 5) Uso de sequencias, quando as chaves dependerem delas.
SELECT c.relname AS sequencia,
       has_sequence_privilege('fugates_acm_hml_app', c.oid, 'USAGE') AS usage_concedido
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
 WHERE c.relkind = 'S'
   AND n.nspname = 'fugates'
   AND (c.relname LIKE 'contrato_sap_toledo%' OR c.relname LIKE 'integracao%')
 ORDER BY c.relname;

COMMIT;

-- Expectativa documental: a aplicacao de ACL de 2026-10-27
-- (docs/Incrementos/UnifiedDev/003_MONITOR_COLUMN_READ_ACL_DEV.sql) concedeu apenas
-- 12 SELECT por COLUNA e declara explicitamente "No writer grants". Portanto o resultado
-- esperado para as acoes INSERT/UPDATE e FALSE, e a devolutiva deve registrar
-- INBOUND_DATABASE_ACL=BLOCKED ate que a Hera autorize os GRANT de escrita.
-- Este arquivo NAO concede nada: a concessao, se autorizada, sera objeto de gate proprio.
