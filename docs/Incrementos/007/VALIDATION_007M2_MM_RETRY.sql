-- READ ONLY candidate physical preflight/postcheck. Never runs migration or custom functions.
BEGIN READ ONLY;
SET LOCAL statement_timeout='10s';SET LOCAL lock_timeout='2s';
SELECT current_database(),current_user,session_user,current_setting('cluster_name'),current_setting('port'),current_setting('transaction_read_only');
SELECT r.rolname,r.rolcanlogin,r.rolsuper,r.rolcreatedb,r.rolcreaterole,r.rolreplication,r.rolbypassrls
 FROM pg_roles r WHERE r.rolname IN('fugates_mm_app','fugates_dev_owner');
SELECT n.nspname,c.relname,c.relkind,pg_get_userbyid(c.relowner) AS owner,c.relacl
 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname='fugates' AND c.relname IN('protocolo_mm_pedido_compra','protocolo_mm_reenvio_comando');
SELECT to_regclass('fugates.protocolo_mm_pedido_compra') AS prerequisite,
 to_regclass('fugates.protocolo_mm_reenvio_comando') AS candidate_table;
SELECT has_table_privilege('fugates_mm_app','fugates.protocolo_mm_pedido_compra','SELECT') AS registry_select,
 has_column_privilege('fugates_mm_app','fugates.protocolo_mm_pedido_compra','estado','UPDATE') AS registry_update_state;
SELECT c.relname,a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull,a.attacl,
 has_column_privilege('fugates_mm_app',c.oid,a.attname,'UPDATE') AS app_can_update
 FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname='fugates' AND c.relname='protocolo_mm_reenvio_comando' AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum;
SELECT i.relname,pg_get_indexdef(i.oid) FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
 JOIN pg_index x ON x.indrelid=t.oid JOIN pg_class i ON i.oid=x.indexrelid
 WHERE n.nspname='fugates' AND t.relname='protocolo_mm_reenvio_comando';
ROLLBACK;
-- Before first apply: candidate table MUST be absent. If present, stop and review receipt/schema; do not recreate/drop.
-- After apply: owner fugates_dev_owner; only owner+MM direct grants; unique partial index includes INDETERMINADO.
-- Prove application login independently using existing protected MM provider and same identity query READ ONLY.
