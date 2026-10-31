BEGIN;

SET LOCAL ROLE fugates_dev_owner;
SET LOCAL search_path TO fugates, pg_catalog;

DO $precheck$
BEGIN
    IF to_regclass('fugates.projecao_resultado_fluxo_mvp') IS NULL THEN
        RAISE EXCEPTION '007A1_ROLLBACK_PRECHECK_FAIL: fugates.projecao_resultado_fluxo_mvp ausente';
    END IF;
END
$precheck$;

DROP TABLE fugates.projecao_resultado_fluxo_mvp;

COMMIT;
