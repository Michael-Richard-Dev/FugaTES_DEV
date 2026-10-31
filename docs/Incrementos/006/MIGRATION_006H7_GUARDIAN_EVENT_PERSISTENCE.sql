BEGIN;

SET LOCAL ROLE fugates_dev_owner;
SET LOCAL search_path TO fugates, pg_catalog;

DO $precheck$
BEGIN
    IF to_regclass('fugates.correlacao_guardian_sap') IS NULL THEN
        RAISE EXCEPTION '006H7_PRECHECK_FAIL: fugates.correlacao_guardian_sap ausente';
    END IF;

    IF to_regclass('fugates.integracao') IS NULL THEN
        RAISE EXCEPTION '006H7_PRECHECK_FAIL: fugates.integracao ausente';
    END IF;

    IF to_regclass('fugates.correlacao_guardian_evento') IS NOT NULL THEN
        RAISE EXCEPTION '006H7_PRECHECK_FAIL: fugates.correlacao_guardian_evento ja existe';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fugates_dev_app') THEN
        RAISE EXCEPTION '006H7_PRECHECK_FAIL: role fugates_dev_app ausente';
    END IF;
END
$precheck$;

CREATE TABLE fugates.correlacao_guardian_evento
(
    id uuid NOT NULL,
    correlacao_id uuid NOT NULL,
    sequencial_guardian bigint NOT NULL,
    estado_codigo integer NOT NULL,
    callback_fingerprint text NOT NULL,
    peso_bruto numeric NULL,
    tara_final numeric NULL,
    peso_liquido numeric NULL,
    integracao_id uuid NULL,
    recebido_em timestamptz NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT pk_correlacao_guardian_evento
        PRIMARY KEY (id),

    CONSTRAINT uq_correlacao_guardian_evento_business_identity
        UNIQUE (correlacao_id, sequencial_guardian, estado_codigo),

    CONSTRAINT fk_correlacao_guardian_evento_correlacao
        FOREIGN KEY (correlacao_id)
        REFERENCES fugates.correlacao_guardian_sap(id)
        ON DELETE RESTRICT,

    CONSTRAINT fk_correlacao_guardian_evento_integracao
        FOREIGN KEY (integracao_id)
        REFERENCES fugates.integracao(id)
        ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED,

    CONSTRAINT ck_correlacao_guardian_evento_estado
        CHECK (estado_codigo IN (1, 3, 5)),

    CONSTRAINT ck_correlacao_guardian_evento_fingerprint
        CHECK (
            length(callback_fingerprint) = 64
            AND callback_fingerprint ~ '^[0-9A-F]{64}$'
        ),

    CONSTRAINT ck_correlacao_guardian_evento_semantica
        CHECK (
            (
                estado_codigo IN (1, 3)
                AND integracao_id IS NULL
                AND peso_bruto IS NULL
                AND tara_final IS NULL
                AND peso_liquido IS NULL
            )
            OR
            (
                estado_codigo = 5
                AND integracao_id IS NOT NULL
            )
        )
);

CREATE UNIQUE INDEX ux_correlacao_guardian_evento_integracao_id
    ON fugates.correlacao_guardian_evento (integracao_id)
    WHERE integracao_id IS NOT NULL;

REVOKE ALL ON TABLE fugates.correlacao_guardian_evento FROM PUBLIC;
REVOKE ALL ON TABLE fugates.correlacao_guardian_evento FROM fugates_dev_app;
GRANT SELECT, INSERT ON TABLE fugates.correlacao_guardian_evento TO fugates_dev_app;

COMMIT;
