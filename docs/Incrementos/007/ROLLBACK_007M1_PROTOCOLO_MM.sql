-- CANDIDATE ONLY — requires explicit authorization. Never remove a registry containing evidence.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
DO $$ BEGIN
 IF current_database()<>'fugates_dev' OR current_setting('port')<>'5433'
    OR current_setting('cluster_name')<>'18/fugatesacmdev' THEN RAISE EXCEPTION 'MM_WRONG_CLUSTER'; END IF;
END $$;
SET LOCAL ROLE fugates_dev_owner;
LOCK TABLE fugates.protocolo_mm_pedido_compra IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM fugates.protocolo_mm_pedido_compra) THEN
  RAISE EXCEPTION 'MM_EVIDENCE_PRESENT_DISABLE_DISPATCH_PRESERVE_REGISTRY';
 END IF;
END $$;
DROP TABLE fugates.protocolo_mm_pedido_compra;
-- Shared schema grants/roles/credentials are intentionally not revoked or removed.
COMMIT;
