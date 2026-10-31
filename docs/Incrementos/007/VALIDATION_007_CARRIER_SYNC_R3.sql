-- GAIA: executar na conexao/perfil aprovado, com fugates.carrier_app_role informado na sessao.
-- Nenhuma mutacao, SET ROLE ou alteracao de configuracao e feita por este arquivo.
BEGIN TRANSACTION READ ONLY;
SELECT current_database() AS database_name,current_user AS validation_user,
       current_setting('fugates.carrier_app_role',true) AS expected_app_role;

WITH expected(name) AS (VALUES ('carrier_sync_cycle'),('carrier_sync_state'),('carrier_sync_attempt'),('carrier_sync_step'),('carrier_sync_checkpoint'),('carrier_sync_request'))
SELECT name,to_regclass('fugates.'||name) IS NOT NULL AS table_present FROM expected;

WITH expected(table_name,column_name) AS (VALUES
 ('carrier_sync_cycle','id'),('carrier_sync_cycle','started_utc'),('carrier_sync_cycle','finished_utc'),('carrier_sync_cycle','status'),
 ('carrier_sync_cycle','consulted'),('carrier_sync_cycle','created'),('carrier_sync_cycle','updated'),('carrier_sync_cycle','unchanged'),('carrier_sync_cycle','pending'),('carrier_sync_cycle','failed'),
 ('carrier_sync_state','bp'),('carrier_sync_state','name'),('carrier_sync_state','guardian_code'),('carrier_sync_state','content_hash'),('carrier_sync_state','status'),('carrier_sync_state','action'),('carrier_sync_state','result_code'),('carrier_sync_state','sap_checked_utc'),('carrier_sync_state','guardian_synced_utc'),('carrier_sync_state','pending_attempt_id'),
 ('carrier_sync_attempt','id'),('carrier_sync_attempt','cycle_id'),('carrier_sync_attempt','bp'),('carrier_sync_attempt','action'),('carrier_sync_attempt','expected_hash'),('carrier_sync_attempt','guardian_code'),('carrier_sync_attempt','started_utc'),('carrier_sync_attempt','resolved_utc'),('carrier_sync_attempt','status'),
 ('carrier_sync_step','id'),('carrier_sync_step','cycle_id'),('carrier_sync_step','bp'),('carrier_sync_step','step'),('carrier_sync_step','result_code'),('carrier_sync_step','occurred_utc'),
 ('carrier_sync_checkpoint','singleton'),('carrier_sync_checkpoint','watermark'),('carrier_sync_checkpoint','upper_bound'),('carrier_sync_checkpoint','page_cursor'),('carrier_sync_checkpoint','visited_pages'),('carrier_sync_checkpoint','enabled'),('carrier_sync_checkpoint','heartbeat_utc'),('carrier_sync_checkpoint','next_cycle_utc'),
 ('carrier_sync_request','id'),('carrier_sync_request','bp'),('carrier_sync_request','authenticated_sid'),('carrier_sync_request','requested_utc'),('carrier_sync_request','status'),('carrier_sync_request','result_code'))
SELECT e.*,c.column_name IS NOT NULL AS column_present,c.data_type,c.is_nullable,c.is_identity,c.identity_generation
FROM expected e LEFT JOIN information_schema.columns c ON c.table_schema='fugates' AND c.table_name=e.table_name AND c.column_name=e.column_name ORDER BY e.table_name,e.column_name;

WITH expected(name,pk_column) AS (VALUES ('carrier_sync_cycle','id'),('carrier_sync_state','bp'),('carrier_sync_attempt','id'),('carrier_sync_step','id'),('carrier_sync_checkpoint','singleton'),('carrier_sync_request','id'))
SELECT e.name,EXISTS(SELECT 1 FROM pg_constraint c JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=c.conkey[1]
 WHERE c.conrelid=to_regclass('fugates.'||e.name) AND c.contype='p' AND cardinality(c.conkey)=1 AND a.attname=e.pk_column) AS primary_key_valid FROM expected e;

-- As quatro FKs devem estar validadas; pending_attempt e DEFERRABLE INITIALLY DEFERRED.
SELECT c.conrelid::regclass AS table_name,c.conname,c.contype,c.convalidated,c.condeferrable,c.condeferred,pg_get_constraintdef(c.oid) AS definition
FROM pg_constraint c WHERE c.connamespace='fugates'::regnamespace AND c.conrelid IN
 (SELECT to_regclass('fugates.'||name) FROM unnest(ARRAY['carrier_sync_cycle','carrier_sync_state','carrier_sync_attempt','carrier_sync_step','carrier_sync_checkpoint','carrier_sync_request']) AS name)
ORDER BY c.conrelid::regclass::text,c.conname;
WITH expected(source_table,column_name,target_table,target_column,deferred) AS (VALUES
 ('carrier_sync_attempt','cycle_id','carrier_sync_cycle','id',false),('carrier_sync_attempt','bp','carrier_sync_state','bp',false),
 ('carrier_sync_state','pending_attempt_id','carrier_sync_attempt','id',true),('carrier_sync_step','cycle_id','carrier_sync_cycle','id',false))
SELECT e.*,EXISTS(SELECT 1 FROM pg_constraint c JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=c.conkey[1]
 JOIN pg_attribute b ON b.attrelid=c.confrelid AND b.attnum=c.confkey[1]
 WHERE c.contype='f' AND c.convalidated AND c.conrelid=to_regclass('fugates.'||e.source_table) AND c.confrelid=to_regclass('fugates.'||e.target_table)
 AND cardinality(c.conkey)=1 AND a.attname=e.column_name AND b.attname=e.target_column AND (NOT e.deferred OR (c.condeferrable AND c.condeferred))) AS foreign_key_valid FROM expected e;

WITH expected(name,min_checks) AS (VALUES ('carrier_sync_cycle',6),('carrier_sync_attempt',1),('carrier_sync_step',2),('carrier_sync_checkpoint',1))
SELECT e.name,count(c.oid)>=e.min_checks AND bool_and(c.convalidated) AS checks_present_validated,count(c.oid) AS check_count
FROM expected e LEFT JOIN pg_constraint c ON c.conrelid=to_regclass('fugates.'||e.name) AND c.contype='c' GROUP BY e.name,e.min_checks;
SELECT i.indisunique AND i.indisvalid AND i.indisready AND i.indnkeyatts=1 AND a.attname='bp'
 AND pg_get_expr(i.indpred,i.indrelid)='(resolved_utc IS NULL)' AS pending_unique_index_valid,pg_get_indexdef(i.indexrelid) AS definition
FROM pg_index i JOIN pg_class x ON x.oid=i.indexrelid JOIN pg_namespace n ON n.oid=x.relnamespace
JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=i.indkey[0]
WHERE n.nspname='fugates' AND x.relname='carrier_sync_one_pending_bp';
SELECT count(*)=1 AND bool_and(singleton IS TRUE) AS singleton_checkpoint_valid FROM fugates.carrier_sync_checkpoint;

WITH app AS (SELECT oid,rolname,rolcanlogin,rolsuper,rolcreaterole,rolcreatedb FROM pg_roles WHERE rolname=nullif(current_setting('fugates.carrier_app_role',true),''))
SELECT a.rolname,a.rolcanlogin AND NOT(a.rolsuper OR a.rolcreaterole OR a.rolcreatedb) AS role_valid,
 has_schema_privilege(a.oid,'fugates','USAGE') AS schema_usage,
 NOT has_schema_privilege(a.oid,'fugates','CREATE') AS schema_create_absent,
 current_setting('fugates.carrier_profile_role',true)=a.rolname AS profile_role_matches,
 nullif(current_setting('fugates.carrier_role_approval',true),'') IS NOT NULL AS approval_present FROM app a;
WITH app AS (SELECT oid,rolname FROM pg_roles WHERE rolname=nullif(current_setting('fugates.carrier_app_role',true),'')),
 expected(name) AS (VALUES ('carrier_sync_cycle'),('carrier_sync_state'),('carrier_sync_attempt'),('carrier_sync_step'),('carrier_sync_checkpoint'),('carrier_sync_request'))
SELECT e.name,a.rolname,a.oid IS NOT NULL AS role_present,
 CASE WHEN a.oid IS NOT NULL AND to_regclass('fugates.'||e.name) IS NOT NULL THEN
 has_table_privilege(a.oid,'fugates.'||e.name,'SELECT') AND has_table_privilege(a.oid,'fugates.'||e.name,'INSERT') AND has_table_privilege(a.oid,'fugates.'||e.name,'UPDATE')
 AND NOT has_table_privilege(a.oid,'fugates.'||e.name,'DELETE') AND NOT has_table_privilege(a.oid,'fugates.'||e.name,'TRUNCATE')
 AND NOT has_table_privilege(a.oid,'fugates.'||e.name,'REFERENCES') AND NOT has_table_privilege(a.oid,'fugates.'||e.name,'TRIGGER') ELSE false END AS effective_grants_valid,
 coalesce(c.relowner<>a.oid,false) AS app_not_owner
FROM expected e LEFT JOIN pg_class c ON c.oid=to_regclass('fugates.'||e.name) LEFT JOIN app a ON true;
SELECT c.relname,acl.privilege_type AS forbidden_public_privilege FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 CROSS JOIN LATERAL aclexplode(coalesce(c.relacl,acldefault(CASE WHEN c.relkind='S' THEN 'S'::"char" ELSE 'r'::"char" END,c.relowner))) acl
 WHERE n.nspname='fugates' AND c.relname IN ('carrier_sync_cycle','carrier_sync_state','carrier_sync_attempt','carrier_sync_step','carrier_sync_checkpoint','carrier_sync_request','carrier_sync_step_id_seq') AND acl.grantee=0;
SELECT a.attidentity='a' AS step_id_generated_always FROM pg_attribute a WHERE a.attrelid='fugates.carrier_sync_step'::regclass AND a.attname='id';
SELECT r.rolname,NOT(has_sequence_privilege(r.oid,'fugates.carrier_sync_step_id_seq','USAGE') OR has_sequence_privilege(r.oid,'fugates.carrier_sync_step_id_seq','SELECT') OR has_sequence_privilege(r.oid,'fugates.carrier_sync_step_id_seq','UPDATE')) AS unnecessary_sequence_grants_absent
FROM pg_roles r WHERE r.rolname=nullif(current_setting('fugates.carrier_app_role',true),'');

-- Tambem inspecionar ACLs por coluna; REVOKE de tabela sozinho nao mascara grants por coluna.
SELECT c.relname,a.attname,acl.privilege_type AS forbidden_public_column_privilege
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
CROSS JOIN LATERAL aclexplode(a.attacl) acl
WHERE n.nspname='fugates' AND c.relname IN ('carrier_sync_cycle','carrier_sync_state','carrier_sync_attempt','carrier_sync_step','carrier_sync_checkpoint','carrier_sync_request') AND acl.grantee=0;
-- Resultado vazio nas consultas de inconsistencias abaixo.
SELECT bp,count(*) FROM fugates.carrier_sync_attempt WHERE resolved_utc IS NULL GROUP BY bp HAVING count(*) > 1;
SELECT s.bp,s.pending_attempt_id FROM fugates.carrier_sync_state s LEFT JOIN fugates.carrier_sync_attempt a ON a.id=s.pending_attempt_id
WHERE s.pending_attempt_id IS NOT NULL AND (a.id IS NULL OR a.bp<>s.bp OR a.resolved_utc IS NOT NULL);
SELECT a.id,a.bp FROM fugates.carrier_sync_attempt a LEFT JOIN fugates.carrier_sync_state s ON s.bp=a.bp
WHERE a.resolved_utc IS NULL AND s.pending_attempt_id IS DISTINCT FROM a.id;
SELECT bp,count(*) FROM fugates.carrier_sync_request WHERE status='PENDING' GROUP BY bp HAVING count(*) > 1;
SELECT singleton,count(*) FROM fugates.carrier_sync_checkpoint GROUP BY singleton HAVING count(*) > 1;
SELECT count(*) AS checkpoint_blocking_states FROM fugates.carrier_sync_state WHERE pending_attempt_id IS NOT NULL OR status IS DISTINCT FROM 'SYNCED';
COMMIT;