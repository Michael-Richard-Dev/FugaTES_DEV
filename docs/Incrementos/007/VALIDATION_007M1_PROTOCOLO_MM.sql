-- READ ONLY CANDIDATE. Inspect every boolean before authorizing migration or live dispatch.
BEGIN READ ONLY;
SET LOCAL statement_timeout='10s';
SET LOCAL lock_timeout='3s';
SELECT current_database() AS database,current_user AS reader_role,session_user,
 current_setting('cluster_name') AS cluster,current_setting('port') AS physical_port,
 current_database()='fugates_dev' AND current_setting('cluster_name')='18/fugatesacmdev'
 AND current_setting('port')='5433' AS expected_destination;
SELECT rolname,rolcanlogin,rolsuper,rolcreatedb,rolcreaterole,rolreplication,rolbypassrls
 FROM pg_roles WHERE rolname IN('fugates_dev_owner','fugates_mm_app');
SELECT nspname,pg_get_userbyid(nspowner) AS schema_owner FROM pg_namespace WHERE nspname='fugates';
SELECT to_regclass('fugates.protocolo_mm_pedido_compra') AS mm_table;
SELECT tablename,tableowner FROM pg_tables WHERE schemaname='fugates' AND tablename='protocolo_mm_pedido_compra';
SELECT grantee,privilege_type FROM information_schema.table_privileges
 WHERE table_schema='fugates' AND table_name='protocolo_mm_pedido_compra' ORDER BY grantee,privilege_type;
SELECT grantee,column_name,privilege_type FROM information_schema.column_privileges
 WHERE table_schema='fugates' AND table_name='protocolo_mm_pedido_compra' ORDER BY grantee,column_name,privilege_type;
SELECT a.pid,a.application_name,a.state,a.backend_type
 FROM pg_stat_activity a WHERE datname='fugates_dev' AND application_name='FugaTES.MM.Protocolo';
ROLLBACK;
-- No secret values, connection strings or business rows are selected.
