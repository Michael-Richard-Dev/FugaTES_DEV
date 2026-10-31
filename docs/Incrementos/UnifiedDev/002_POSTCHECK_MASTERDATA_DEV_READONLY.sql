BEGIN READ ONLY;
SET LOCAL statement_timeout='10s';
SET LOCAL lock_timeout='1s';

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

SELECT 'UNIFIED_DEV_MASTERDATA_POSTCHECK_PASS';
ROLLBACK;
