-- CANDIDATE ONLY. Stop command admission and drain MM first. Preserve all nonempty audit.
BEGIN;
SET LOCAL lock_timeout='5s';SET LOCAL statement_timeout='30s';
DO $$ BEGIN
 IF current_database()<>'fugates_dev' OR current_setting('port')<>'5433' OR current_setting('cluster_name')<>'18/fugatesacmdev' THEN RAISE EXCEPTION 'MM_WRONG_CLUSTER';END IF;
END $$;
SET LOCAL ROLE fugates_dev_owner;
-- Lock BEFORE checking emptiness: no insert may commit between the guard and DROP.
LOCK TABLE fugates.protocolo_mm_reenvio_comando IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM fugates.protocolo_mm_reenvio_comando) THEN RAISE EXCEPTION 'MM_RETRY_AUDIT_PRESENT_DO_NOT_DROP';END IF;
END $$;
DROP TABLE fugates.protocolo_mm_reenvio_comando;
COMMIT;
-- If audit exists: rollback only binaries/configuration; retain table/outcomes. Never delete commands to allow replay.
