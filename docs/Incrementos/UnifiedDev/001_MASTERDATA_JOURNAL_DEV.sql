-- FUGATES-UNIFIED-POSTGRESQL-DEV. Target-only additive journal; no business/reference rows copied.
-- Derived from canonical approved R150A and physical PG18 source contract; source 5434 remains read-only.
-- Execute only after verified current backup, writer exclusion and independent review. No blind replay after unknown COMMIT.
BEGIN;
SET TRANSACTION READ WRITE;
SET LOCAL lock_timeout='3s';
SET LOCAL statement_timeout='60s';
SET LOCAL search_path=pg_catalog;
DO $target_guard$
BEGIN
 IF current_database()<>'fugates_dev' OR current_setting('port')<>'5433'
    OR current_setting('cluster_name')<>'18/fugatesacmdev' OR pg_is_in_recovery()
    OR current_setting('server_version_num')::integer NOT BETWEEN 180000 AND 189999
    OR session_user<>'postgres' OR current_user<>'postgres'
    OR to_regclass('fugates.contrato_sap_toledo_versao') IS NULL
    OR to_regclass('fugates.carrier_single_create_journal') IS NOT NULL THEN
  RAISE EXCEPTION 'UNIFIED_DEV_TARGET_OR_BASELINE_MISMATCH';
 END IF;
 IF NOT EXISTS(select 1 from pg_roles where rolname='fugates_dev_owner' and not rolcanlogin and not rolsuper and not rolcreaterole and not rolcreatedb)
    OR NOT EXISTS(select 1 from pg_roles where rolname='fugates_acm_hml_app' and rolcanlogin and not rolsuper and not rolcreaterole and not rolcreatedb and not rolbypassrls)
    OR pg_has_role('fugates_acm_hml_app','fugates_dev_owner','SET') THEN
  RAISE EXCEPTION 'UNIFIED_DEV_ROLE_ISOLATION_INVALID';
 END IF;
 IF EXISTS(select 1 from pg_namespace where nspname='masterdata' and pg_get_userbyid(nspowner)<>'fugates_dev_owner') THEN
  RAISE EXCEPTION 'UNIFIED_DEV_EXISTING_SCHEMA_OWNER_MISMATCH';
 END IF;
 IF EXISTS(select 1 from pg_class c join pg_namespace n on n.oid=c.relnamespace where n.nspname='masterdata'
     and c.relname not in ('carrier_single_create_journal','carrier_single_create_journal_pkey','carrier_single_create_journal_bp_uk','carrier_single_create_journal_code_uk'))
    OR EXISTS(select 1 from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='masterdata')
    OR EXISTS(select 1 from pg_default_acl d cross join lateral aclexplode(d.defaclacl) a
      where d.defaclrole=(select oid from pg_roles where rolname='fugates_dev_owner')
       and (d.defaclnamespace=0 or d.defaclnamespace=(select oid from pg_namespace where nspname='masterdata'))
       and a.grantee<>d.defaclrole)
    OR EXISTS(select 1 from pg_namespace n cross join lateral aclexplode(coalesce(n.nspacl,acldefault('n',n.nspowner))) a
      where n.nspname='masterdata' and a.grantee<>n.nspowner
       and not(a.grantee=(select oid from pg_roles where rolname='fugates_acm_hml_app') and a.privilege_type='USAGE' and not a.is_grantable)) THEN
  RAISE EXCEPTION 'UNIFIED_DEV_EXISTING_SCHEMA_OR_DEFAULT_ACL_DRIFT';
 END IF;
END; $target_guard$;

DO $validate_masterdata$
BEGIN
 IF to_regclass('masterdata.carrier_single_create_journal') IS NULL THEN RETURN; END IF;
 IF current_database()<>'fugates_dev' OR current_setting('port')<>'5433'
    OR current_setting('cluster_name')<>'18/fugatesacmdev' THEN
  RAISE EXCEPTION 'UNIFIED_DEV_TARGET_MISMATCH';
 END IF;
 IF (select pg_get_userbyid(nspowner) from pg_namespace where nspname='masterdata') IS DISTINCT FROM 'fugates_dev_owner'
    OR (select pg_get_userbyid(relowner) from pg_class where oid='masterdata.carrier_single_create_journal'::regclass) IS DISTINCT FROM 'fugates_dev_owner' THEN
  RAISE EXCEPTION 'UNIFIED_DEV_OWNER_MISMATCH';
 END IF;
 IF (select count(*) from pg_attribute where attrelid='masterdata.carrier_single_create_journal'::regclass and attnum>0 and not attisdropped)<>7
    OR EXISTS(
      SELECT 1 FROM (VALUES ('attempt_id','uuid',true,1),
('bp','character varying(40)',true,2),
('guardian_code','character varying(20)',true,3),
('payload_hash','character(64)',true,4),
('status','character varying(20)',true,5),
('prepared_utc','timestamp with time zone',true,6),
('confirmed_utc','timestamp with time zone',false,7)) e(name,type_name,not_null,ordinal)
      LEFT JOIN pg_attribute a ON a.attrelid='masterdata.carrier_single_create_journal'::regclass AND a.attname=e.name
      WHERE a.attnum IS DISTINCT FROM e.ordinal OR format_type(a.atttypid,a.atttypmod) IS DISTINCT FROM e.type_name
       OR a.attnotnull IS DISTINCT FROM e.not_null OR a.attidentity<>'' OR a.attgenerated<>''
       OR EXISTS(select 1 from pg_attrdef d where d.adrelid=a.attrelid and d.adnum=a.attnum)) THEN
  RAISE EXCEPTION 'UNIFIED_DEV_COLUMN_CONTRACT_MISMATCH';
 END IF;
 IF (select count(*) from pg_constraint where conrelid='masterdata.carrier_single_create_journal'::regclass)<>11
    OR EXISTS(
      SELECT 1 FROM (VALUES ('carrier_single_create_journal_attempt_id_not_null','n','NOT NULL attempt_id',false),
('carrier_single_create_journal_bp_not_null','n','NOT NULL bp',false),
('carrier_single_create_journal_check','c','CHECK ((status::text = ''CONFIRMED''::text) = (confirmed_utc IS NOT NULL))',false),
('carrier_single_create_journal_check1','c','CHECK (confirmed_utc IS NULL OR confirmed_utc >= prepared_utc)',false),
('carrier_single_create_journal_guardian_code_not_null','n','NOT NULL guardian_code',false),
('carrier_single_create_journal_payload_hash_check','c','CHECK (payload_hash ~ ''\A[0-9a-f]{64}\Z''::text)',false),
('carrier_single_create_journal_payload_hash_not_null','n','NOT NULL payload_hash',false),
('carrier_single_create_journal_pkey','p','PRIMARY KEY (attempt_id)',true),
('carrier_single_create_journal_prepared_utc_not_null','n','NOT NULL prepared_utc',false),
('carrier_single_create_journal_status_check','c','CHECK (status::text = ANY (ARRAY[''PREPARED''::character varying, ''CONFIRMED''::character varying]::text[]))',false),
('carrier_single_create_journal_status_not_null','n','NOT NULL status',false)) e(name,kind,definition,no_inherit)
      LEFT JOIN pg_constraint k ON k.conrelid='masterdata.carrier_single_create_journal'::regclass AND k.conname=e.name
      WHERE k.contype::text IS DISTINCT FROM e.kind
       OR regexp_replace(pg_get_constraintdef(k.oid,true),'[[:space:]]','','g') IS DISTINCT FROM regexp_replace(e.definition,'[[:space:]]','','g')
       OR k.connoinherit IS DISTINCT FROM e.no_inherit OR NOT k.convalidated OR NOT k.conenforced
       OR k.condeferrable OR k.condeferred) THEN
  RAISE EXCEPTION 'UNIFIED_DEV_CONSTRAINT_CONTRACT_MISMATCH';
 END IF;
 IF (select count(*) from pg_index where indrelid='masterdata.carrier_single_create_journal'::regclass)<>3
    OR EXISTS(select 1 from pg_index where indrelid='masterdata.carrier_single_create_journal'::regclass
       and (not indisunique or not indisvalid or not indisready or indpred is not null or indexprs is not null))
    OR NOT EXISTS(select 1 from pg_indexes where schemaname='masterdata' and tablename='carrier_single_create_journal'
        and indexname='carrier_single_create_journal_bp_uk' and indexdef='CREATE UNIQUE INDEX carrier_single_create_journal_bp_uk ON masterdata.carrier_single_create_journal USING btree (bp)')
    OR NOT EXISTS(select 1 from pg_indexes where schemaname='masterdata' and tablename='carrier_single_create_journal'
        and indexname='carrier_single_create_journal_code_uk' and indexdef='CREATE UNIQUE INDEX carrier_single_create_journal_code_uk ON masterdata.carrier_single_create_journal USING btree (guardian_code)') THEN
  RAISE EXCEPTION 'UNIFIED_DEV_INDEX_CONTRACT_MISMATCH';
 END IF;
 IF NOT has_schema_privilege('fugates_acm_hml_app','masterdata','USAGE')
    OR has_schema_privilege('fugates_acm_hml_app','masterdata','CREATE')
    OR NOT has_table_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','SELECT')
    OR has_any_column_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','INSERT')
    OR has_any_column_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','UPDATE')
    OR has_any_column_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','REFERENCES')
    OR has_table_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','DELETE,TRUNCATE,REFERENCES,TRIGGER')
    OR has_table_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','MAINTAIN')
    OR pg_has_role('fugates_acm_hml_app','fugates_dev_owner','SET') THEN
  RAISE EXCEPTION 'UNIFIED_DEV_READ_ROLE_PRIVILEGES_INVALID';
 END IF;
 IF EXISTS(select 1 from pg_class c cross join lateral aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) a
      where c.oid='masterdata.carrier_single_create_journal'::regclass and a.grantee<>c.relowner
       and not(a.grantee=(select oid from pg_roles where rolname='fugates_acm_hml_app') and a.privilege_type='SELECT' and not a.is_grantable))
    OR EXISTS(select 1 from pg_namespace n cross join lateral aclexplode(coalesce(n.nspacl,acldefault('n',n.nspowner))) a
      where n.nspname='masterdata' and a.grantee<>n.nspowner
       and not(a.grantee=(select oid from pg_roles where rolname='fugates_acm_hml_app') and a.privilege_type='USAGE' and not a.is_grantable))
    OR EXISTS(select 1 from pg_attribute where attrelid='masterdata.carrier_single_create_journal'::regclass
       and attnum>0 and not attisdropped and coalesce(cardinality(attacl),0)<>0)
    OR EXISTS(select 1 from pg_default_acl d cross join lateral aclexplode(d.defaclacl) a
      where d.defaclrole=(select oid from pg_roles where rolname='fugates_dev_owner')
       and (d.defaclnamespace=0 or d.defaclnamespace=(select oid from pg_namespace where nspname='masterdata'))
       and a.grantee<>d.defaclrole) THEN
  RAISE EXCEPTION 'UNIFIED_DEV_UNEXPECTED_ACL_OR_DEFAULT_PRIVILEGE';
 END IF;
 IF EXISTS(select 1 from pg_class where oid='masterdata.carrier_single_create_journal'::regclass and (relrowsecurity or relforcerowsecurity))
    OR EXISTS(select 1 from pg_trigger where tgrelid='masterdata.carrier_single_create_journal'::regclass)
    OR to_regclass('fugates.carrier_single_create_journal') IS NOT NULL THEN
  RAISE EXCEPTION 'UNIFIED_DEV_UNEXPECTED_BEHAVIOR_OR_DUPLICATE';
 END IF;
END;
$validate_masterdata$;

SELECT pg_advisory_xact_lock(1655433,2701);
CREATE SCHEMA IF NOT EXISTS masterdata AUTHORIZATION fugates_dev_owner;
SET LOCAL ROLE fugates_dev_owner;
REVOKE ALL ON SCHEMA masterdata FROM PUBLIC, fugates_dev_app;
GRANT USAGE ON SCHEMA masterdata TO fugates_acm_hml_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA masterdata REVOKE ALL ON TABLES FROM PUBLIC, fugates_dev_app;
CREATE TABLE IF NOT EXISTS masterdata.carrier_single_create_journal (
 attempt_id uuid PRIMARY KEY,
 bp varchar(40) NOT NULL,
 guardian_code varchar(20) NOT NULL,
 payload_hash char(64) NOT NULL CHECK(payload_hash ~ E'\\A[0-9a-f]{64}\\Z'),
 status varchar(20) NOT NULL CHECK(status IN('PREPARED','CONFIRMED')),
 prepared_utc timestamptz NOT NULL,
 confirmed_utc timestamptz,
 CHECK((status='CONFIRMED') = (confirmed_utc IS NOT NULL)),
 CHECK(confirmed_utc IS NULL OR confirmed_utc>=prepared_utc)
);
CREATE UNIQUE INDEX IF NOT EXISTS carrier_single_create_journal_bp_uk ON masterdata.carrier_single_create_journal USING btree (bp);
CREATE UNIQUE INDEX IF NOT EXISTS carrier_single_create_journal_code_uk ON masterdata.carrier_single_create_journal USING btree (guardian_code);
REVOKE ALL ON TABLE masterdata.carrier_single_create_journal FROM PUBLIC, fugates_dev_app, fugates_acm_hml_app;
GRANT SELECT ON TABLE masterdata.carrier_single_create_journal TO fugates_acm_hml_app;

DO $validate_masterdata$
BEGIN
 IF current_database()<>'fugates_dev' OR current_setting('port')<>'5433'
    OR current_setting('cluster_name')<>'18/fugatesacmdev' THEN
  RAISE EXCEPTION 'UNIFIED_DEV_TARGET_MISMATCH';
 END IF;
 IF (select pg_get_userbyid(nspowner) from pg_namespace where nspname='masterdata') IS DISTINCT FROM 'fugates_dev_owner'
    OR (select pg_get_userbyid(relowner) from pg_class where oid='masterdata.carrier_single_create_journal'::regclass) IS DISTINCT FROM 'fugates_dev_owner' THEN
  RAISE EXCEPTION 'UNIFIED_DEV_OWNER_MISMATCH';
 END IF;
 IF (select count(*) from pg_attribute where attrelid='masterdata.carrier_single_create_journal'::regclass and attnum>0 and not attisdropped)<>7
    OR EXISTS(
      SELECT 1 FROM (VALUES ('attempt_id','uuid',true,1),
('bp','character varying(40)',true,2),
('guardian_code','character varying(20)',true,3),
('payload_hash','character(64)',true,4),
('status','character varying(20)',true,5),
('prepared_utc','timestamp with time zone',true,6),
('confirmed_utc','timestamp with time zone',false,7)) e(name,type_name,not_null,ordinal)
      LEFT JOIN pg_attribute a ON a.attrelid='masterdata.carrier_single_create_journal'::regclass AND a.attname=e.name
      WHERE a.attnum IS DISTINCT FROM e.ordinal OR format_type(a.atttypid,a.atttypmod) IS DISTINCT FROM e.type_name
       OR a.attnotnull IS DISTINCT FROM e.not_null OR a.attidentity<>'' OR a.attgenerated<>''
       OR EXISTS(select 1 from pg_attrdef d where d.adrelid=a.attrelid and d.adnum=a.attnum)) THEN
  RAISE EXCEPTION 'UNIFIED_DEV_COLUMN_CONTRACT_MISMATCH';
 END IF;
 IF (select count(*) from pg_constraint where conrelid='masterdata.carrier_single_create_journal'::regclass)<>11
    OR EXISTS(
      SELECT 1 FROM (VALUES ('carrier_single_create_journal_attempt_id_not_null','n','NOT NULL attempt_id',false),
('carrier_single_create_journal_bp_not_null','n','NOT NULL bp',false),
('carrier_single_create_journal_check','c','CHECK ((status::text = ''CONFIRMED''::text) = (confirmed_utc IS NOT NULL))',false),
('carrier_single_create_journal_check1','c','CHECK (confirmed_utc IS NULL OR confirmed_utc >= prepared_utc)',false),
('carrier_single_create_journal_guardian_code_not_null','n','NOT NULL guardian_code',false),
('carrier_single_create_journal_payload_hash_check','c','CHECK (payload_hash ~ ''\A[0-9a-f]{64}\Z''::text)',false),
('carrier_single_create_journal_payload_hash_not_null','n','NOT NULL payload_hash',false),
('carrier_single_create_journal_pkey','p','PRIMARY KEY (attempt_id)',true),
('carrier_single_create_journal_prepared_utc_not_null','n','NOT NULL prepared_utc',false),
('carrier_single_create_journal_status_check','c','CHECK (status::text = ANY (ARRAY[''PREPARED''::character varying, ''CONFIRMED''::character varying]::text[]))',false),
('carrier_single_create_journal_status_not_null','n','NOT NULL status',false)) e(name,kind,definition,no_inherit)
      LEFT JOIN pg_constraint k ON k.conrelid='masterdata.carrier_single_create_journal'::regclass AND k.conname=e.name
      WHERE k.contype::text IS DISTINCT FROM e.kind
       OR regexp_replace(pg_get_constraintdef(k.oid,true),'[[:space:]]','','g') IS DISTINCT FROM regexp_replace(e.definition,'[[:space:]]','','g')
       OR k.connoinherit IS DISTINCT FROM e.no_inherit OR NOT k.convalidated OR NOT k.conenforced
       OR k.condeferrable OR k.condeferred) THEN
  RAISE EXCEPTION 'UNIFIED_DEV_CONSTRAINT_CONTRACT_MISMATCH';
 END IF;
 IF (select count(*) from pg_index where indrelid='masterdata.carrier_single_create_journal'::regclass)<>3
    OR EXISTS(select 1 from pg_index where indrelid='masterdata.carrier_single_create_journal'::regclass
       and (not indisunique or not indisvalid or not indisready or indpred is not null or indexprs is not null))
    OR NOT EXISTS(select 1 from pg_indexes where schemaname='masterdata' and tablename='carrier_single_create_journal'
        and indexname='carrier_single_create_journal_bp_uk' and indexdef='CREATE UNIQUE INDEX carrier_single_create_journal_bp_uk ON masterdata.carrier_single_create_journal USING btree (bp)')
    OR NOT EXISTS(select 1 from pg_indexes where schemaname='masterdata' and tablename='carrier_single_create_journal'
        and indexname='carrier_single_create_journal_code_uk' and indexdef='CREATE UNIQUE INDEX carrier_single_create_journal_code_uk ON masterdata.carrier_single_create_journal USING btree (guardian_code)') THEN
  RAISE EXCEPTION 'UNIFIED_DEV_INDEX_CONTRACT_MISMATCH';
 END IF;
 IF NOT has_schema_privilege('fugates_acm_hml_app','masterdata','USAGE')
    OR has_schema_privilege('fugates_acm_hml_app','masterdata','CREATE')
    OR NOT has_table_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','SELECT')
    OR has_any_column_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','INSERT')
    OR has_any_column_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','UPDATE')
    OR has_any_column_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','REFERENCES')
    OR has_table_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','DELETE,TRUNCATE,REFERENCES,TRIGGER')
    OR has_table_privilege('fugates_acm_hml_app','masterdata.carrier_single_create_journal','MAINTAIN')
    OR pg_has_role('fugates_acm_hml_app','fugates_dev_owner','SET') THEN
  RAISE EXCEPTION 'UNIFIED_DEV_READ_ROLE_PRIVILEGES_INVALID';
 END IF;
 IF EXISTS(select 1 from pg_class c cross join lateral aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) a
      where c.oid='masterdata.carrier_single_create_journal'::regclass and a.grantee<>c.relowner
       and not(a.grantee=(select oid from pg_roles where rolname='fugates_acm_hml_app') and a.privilege_type='SELECT' and not a.is_grantable))
    OR EXISTS(select 1 from pg_namespace n cross join lateral aclexplode(coalesce(n.nspacl,acldefault('n',n.nspowner))) a
      where n.nspname='masterdata' and a.grantee<>n.nspowner
       and not(a.grantee=(select oid from pg_roles where rolname='fugates_acm_hml_app') and a.privilege_type='USAGE' and not a.is_grantable))
    OR EXISTS(select 1 from pg_attribute where attrelid='masterdata.carrier_single_create_journal'::regclass
       and attnum>0 and not attisdropped and coalesce(cardinality(attacl),0)<>0)
    OR EXISTS(select 1 from pg_default_acl d cross join lateral aclexplode(d.defaclacl) a
      where d.defaclrole=(select oid from pg_roles where rolname='fugates_dev_owner')
       and (d.defaclnamespace=0 or d.defaclnamespace=(select oid from pg_namespace where nspname='masterdata'))
       and a.grantee<>d.defaclrole) THEN
  RAISE EXCEPTION 'UNIFIED_DEV_UNEXPECTED_ACL_OR_DEFAULT_PRIVILEGE';
 END IF;
 IF EXISTS(select 1 from pg_class where oid='masterdata.carrier_single_create_journal'::regclass and (relrowsecurity or relforcerowsecurity))
    OR EXISTS(select 1 from pg_trigger where tgrelid='masterdata.carrier_single_create_journal'::regclass)
    OR to_regclass('fugates.carrier_single_create_journal') IS NOT NULL THEN
  RAISE EXCEPTION 'UNIFIED_DEV_UNEXPECTED_BEHAVIOR_OR_DUPLICATE';
 END IF;
END;
$validate_masterdata$;

COMMIT;
