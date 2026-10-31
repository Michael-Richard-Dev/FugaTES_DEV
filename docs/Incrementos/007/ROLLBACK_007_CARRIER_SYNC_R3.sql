-- HOTFIX R2. PRE-OPERATION ONLY. GAIA must stop/disable Carrier worker first.
-- Required session assertion after SCM verification: fugates.carrier_worker_disabled=true.
-- After any operation use an additive recovery; this file must not destroy history.
BEGIN;
DO $carrier_worker_guard$
BEGIN
 IF current_setting('fugates.carrier_worker_disabled',true) IS DISTINCT FROM 'true' THEN
  RAISE EXCEPTION 'CARRIER_ROLLBACK_WORKER_NOT_DISABLED';
 END IF;
 IF NOT pg_try_advisory_xact_lock(1179993940,3) THEN
  RAISE EXCEPTION 'CARRIER_ROLLBACK_ACTIVE_LEASE';
 END IF;
END; $carrier_worker_guard$;
LOCK TABLE fugates.carrier_sync_cycle,fugates.carrier_sync_state,fugates.carrier_sync_attempt,fugates.carrier_sync_step,fugates.carrier_sync_checkpoint,fugates.carrier_sync_request IN ACCESS EXCLUSIVE MODE NOWAIT;
DO $carrier_empty_guard$
BEGIN
 IF EXISTS (SELECT 1 FROM fugates.carrier_sync_cycle)
 OR EXISTS (SELECT 1 FROM fugates.carrier_sync_state)
 OR EXISTS (SELECT 1 FROM fugates.carrier_sync_attempt)
 OR EXISTS (SELECT 1 FROM fugates.carrier_sync_step)
 OR EXISTS (SELECT 1 FROM fugates.carrier_sync_request)
 OR (SELECT count(*) FROM fugates.carrier_sync_checkpoint) <> 1
 OR EXISTS (SELECT 1 FROM fugates.carrier_sync_checkpoint WHERE singleton IS DISTINCT FROM true
    OR watermark IS NOT NULL OR upper_bound IS NOT NULL OR page_cursor IS NOT NULL
    OR visited_pages IS DISTINCT FROM ARRAY[]::text[] OR enabled IS DISTINCT FROM false
    OR heartbeat_utc IS NOT NULL OR next_cycle_utc IS NOT NULL)
 OR (SELECT is_called FROM fugates.carrier_sync_step_id_seq) THEN
  RAISE EXCEPTION 'CARRIER_ROLLBACK_OPERATIONAL_DATA';
 END IF;
END; $carrier_empty_guard$;
DROP TABLE fugates.carrier_sync_request;
DROP TABLE fugates.carrier_sync_checkpoint;
DROP TABLE fugates.carrier_sync_step;
ALTER TABLE fugates.carrier_sync_state DROP CONSTRAINT carrier_pending_attempt_fk;
DROP TABLE fugates.carrier_sync_attempt;
DROP TABLE fugates.carrier_sync_state;
DROP TABLE fugates.carrier_sync_cycle;
COMMIT;