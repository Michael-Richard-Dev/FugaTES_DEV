-- REVIEW ONLY: no connection or execution was performed in this gate.
-- Stop unless this physical identity equals the operator-approved destination.
BEGIN READ ONLY;
SET LOCAL statement_timeout = '5s';
SET LOCAL lock_timeout = '1s';
SELECT current_database(), session_user, current_user,
       current_setting('cluster_name') AS cluster_name,
       current_setting('port') AS physical_port,
       inet_server_addr(), current_setting('transaction_read_only') AS read_only;
SELECT to_regnamespace('fugates') AS expected_schema,
       to_regclass('fugates.toledo_pesagem_recebida') AS proposed_inbox;
SELECT n.nspname, c.relname, pg_get_userbyid(c.relowner) AS owner
FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname='fugates' AND c.relname='toledo_pesagem_recebida';
ROLLBACK;
