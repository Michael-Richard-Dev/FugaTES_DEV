BEGIN;

SET LOCAL ROLE fugates_dev_owner;
SET LOCAL search_path TO fugates, pg_catalog;

DO $rollback$
DECLARE
    v_rows bigint;
BEGIN
    IF to_regclass('fugates.correlacao_guardian_evento') IS NULL THEN
        RAISE NOTICE '006H7_ROLLBACK_SAFE_NOOP: fugates.correlacao_guardian_evento nao existe';
        RETURN;
    END IF;

    LOCK TABLE fugates.correlacao_guardian_evento IN ACCESS EXCLUSIVE MODE;

    SELECT count(*)
      INTO v_rows
      FROM fugates.correlacao_guardian_evento;

    IF v_rows > 0 THEN
        RAISE EXCEPTION '006H7_ROLLBACK_ABORT: ledger possui % linha(s); zero DROP', v_rows;
    END IF;

    DROP TABLE fugates.correlacao_guardian_evento;
END
$rollback$;

COMMIT;
