-- R161 - Identidade exclusiva de escrita do receptor SAP no DEV 5433.
--
-- Escopo: CREATE ROLE + privilegios minimos validados em R160. Aditivo e reversivel.
-- NAO altera ownership, NAO altera LOGIN de roles existentes, NAO concede DELETE, TRUNCATE,
-- UPDATE irrestrito, privilegio de worker ou superuser. Nada e tocado em 5432 ou 5434.
--
-- A senha NUNCA aparece neste arquivo: ela e passada por :'senha' a partir do terminal do
-- operador, que a gera localmente e a grava no pgpass com modo 600.
--
-- Aplicar apenas apos backup fisico verificado, em sessao interativa do operador.

\set ON_ERROR_STOP on

BEGIN;
SET TRANSACTION READ WRITE;
SET LOCAL statement_timeout='15s';
SET LOCAL lock_timeout='5s';
SET LOCAL search_path=pg_catalog;

-- ---------------------------------------------------------------------------
-- 1) Guarda de destino. Qualquer divergencia aborta antes de qualquer escrita.
-- ---------------------------------------------------------------------------
DO $guarda$
BEGIN
  IF current_database() <> 'fugates_dev'
     OR current_setting('port') <> '5433'
     OR current_setting('cluster_name') <> '18/fugatesacmdev'
     OR pg_is_in_recovery()
     OR current_setting('server_version_num')::integer NOT BETWEEN 180000 AND 189999
     OR session_user <> 'postgres' OR current_user <> 'postgres' THEN
    RAISE EXCEPTION 'SAP_WRITER_TARGET_REJECTED';
  END IF;

  -- As tres tabelas tem que existir e pertencer ao owner esperado.
  IF EXISTS (
    SELECT 1 FROM pg_class c
     WHERE c.oid IN ('fugates.contrato_sap_toledo_identidade'::regclass,
                     'fugates.contrato_sap_toledo_versao'::regclass,
                     'fugates.integracao'::regclass)
       AND pg_get_userbyid(c.relowner) <> 'fugates_dev_owner') THEN
    RAISE EXCEPTION 'SAP_WRITER_OWNER_REJECTED';
  END IF;

  -- A role nao pode ja existir: este script cria, nunca reconfigura.
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='fugates_sap_inbound_app') THEN
    RAISE EXCEPTION 'SAP_WRITER_ROLE_ALREADY_EXISTS';
  END IF;
END; $guarda$;

-- ---------------------------------------------------------------------------
-- 2) Role dedicada. Sem atributos privilegiados, sem heranca de owner.
-- ---------------------------------------------------------------------------
CREATE ROLE fugates_sap_inbound_app
  LOGIN
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS
  CONNECTION LIMIT 4
  PASSWORD :'senha';

-- ---------------------------------------------------------------------------
-- 3) Privilegios minimos, exatamente os validados em R160.
--    Espelha a ACL de runtime que a migracao 006H4 ja aplica a fugates_dev_app.
-- ---------------------------------------------------------------------------
SET LOCAL ROLE fugates_dev_owner;

GRANT USAGE ON SCHEMA fugates TO fugates_sap_inbound_app;

GRANT SELECT, INSERT ON fugates.contrato_sap_toledo_identidade TO fugates_sap_inbound_app;
GRANT UPDATE (ultima_versao) ON fugates.contrato_sap_toledo_identidade TO fugates_sap_inbound_app;

GRANT SELECT, INSERT ON fugates.contrato_sap_toledo_versao TO fugates_sap_inbound_app;

GRANT INSERT ON fugates.integracao TO fugates_sap_inbound_app;
GRANT SELECT (id, correlation_id, criado_em, origem, destino,
              operacao, chave_negocio, fingerprint_sha256)
  ON fugates.integracao TO fugates_sap_inbound_app;

RESET ROLE;

-- ---------------------------------------------------------------------------
-- 4) Postcheck. Confirma o que foi concedido E recusa o que nao foi previsto.
-- ---------------------------------------------------------------------------
DO $postcheck$
DECLARE faltando text; excedente text;
BEGIN
  -- 4.1 tudo que o receptor precisa tem que estar presente
  SELECT string_agg(format('%s:%s', v.obj, v.acao), ', ')
    INTO faltando
    FROM (VALUES
      ('fugates.contrato_sap_toledo_identidade','SELECT'),
      ('fugates.contrato_sap_toledo_identidade','INSERT'),
      ('fugates.contrato_sap_toledo_versao','SELECT'),
      ('fugates.contrato_sap_toledo_versao','INSERT'),
      ('fugates.integracao','INSERT')
    ) v(obj,acao)
   WHERE NOT has_table_privilege('fugates_sap_inbound_app', v.obj, v.acao);
  IF faltando IS NOT NULL THEN
    RAISE EXCEPTION 'SAP_WRITER_GRANT_INCOMPLETE: %', faltando;
  END IF;

  IF NOT has_column_privilege('fugates_sap_inbound_app','fugates.contrato_sap_toledo_identidade','ultima_versao','UPDATE')
     OR NOT has_schema_privilege('fugates_sap_inbound_app','fugates','USAGE') THEN
    RAISE EXCEPTION 'SAP_WRITER_GRANT_INCOMPLETE: ultima_versao/USAGE';
  END IF;

  SELECT string_agg(a.attname, ', ') INTO faltando
    FROM unnest(ARRAY['id','correlation_id','criado_em','origem','destino',
                      'operacao','chave_negocio','fingerprint_sha256']) a(attname)
   WHERE NOT has_column_privilege('fugates_sap_inbound_app','fugates.integracao',a.attname,'SELECT');
  IF faltando IS NOT NULL THEN
    RAISE EXCEPTION 'SAP_WRITER_GRANT_INCOMPLETE: integracao.SELECT(%)', faltando;
  END IF;

  -- 4.2 nada alem do previsto
  SELECT string_agg(format('%s:%s', v.obj, v.acao), ', ')
    INTO excedente
    FROM (VALUES
      ('fugates.contrato_sap_toledo_identidade','DELETE'),
      ('fugates.contrato_sap_toledo_identidade','TRUNCATE'),
      ('fugates.contrato_sap_toledo_versao','UPDATE'),
      ('fugates.contrato_sap_toledo_versao','DELETE'),
      ('fugates.contrato_sap_toledo_versao','TRUNCATE'),
      ('fugates.integracao','UPDATE'),
      ('fugates.integracao','DELETE'),
      ('fugates.integracao','TRUNCATE'),
      ('fugates.tentativa_integracao','SELECT'),
      ('fugates.tentativa_integracao','INSERT'),
      ('fugates.auditoria_integracao','SELECT'),
      ('fugates.auditoria_integracao','INSERT')
    ) v(obj,acao)
   WHERE has_table_privilege('fugates_sap_inbound_app', v.obj, v.acao);
  IF excedente IS NOT NULL THEN
    RAISE EXCEPTION 'SAP_WRITER_GRANT_EXCESS: %', excedente;
  END IF;

  -- 4.3 UPDATE restrito: nenhuma outra coluna de identidade pode ser atualizavel
  SELECT string_agg(a.attname, ', ') INTO excedente
    FROM pg_attribute a
   WHERE a.attrelid='fugates.contrato_sap_toledo_identidade'::regclass
     AND a.attnum>0 AND NOT a.attisdropped AND a.attname <> 'ultima_versao'
     AND has_column_privilege('fugates_sap_inbound_app','fugates.contrato_sap_toledo_identidade',a.attname,'UPDATE');
  IF excedente IS NOT NULL THEN
    RAISE EXCEPTION 'SAP_WRITER_UPDATE_NOT_RESTRICTED: %', excedente;
  END IF;

  -- 4.4 a role nao pode ser privilegiada nem membro do owner
  IF EXISTS (SELECT 1 FROM pg_roles
              WHERE rolname='fugates_sap_inbound_app'
                AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls OR rolreplication))
     OR pg_has_role('fugates_sap_inbound_app','fugates_dev_owner','USAGE') THEN
    RAISE EXCEPTION 'SAP_WRITER_ROLE_OVERPRIVILEGED';
  END IF;
END; $postcheck$;

COMMIT;

-- ---------------------------------------------------------------------------
-- Rollback (arquivo separado): 004_ROLLBACK_SAP_INBOUND_WRITER_ROLE_DEV.sql
-- ---------------------------------------------------------------------------
