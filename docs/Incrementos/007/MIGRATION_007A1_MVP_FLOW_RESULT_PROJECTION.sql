BEGIN;

SET LOCAL ROLE fugates_dev_owner;
SET LOCAL search_path TO fugates, pg_catalog;

DO $precheck$
BEGIN
    IF to_regclass('fugates.integracao') IS NULL THEN
        RAISE EXCEPTION '007A1_PRECHECK_FAIL: fugates.integracao ausente';
    END IF;

    IF to_regclass('fugates.projecao_resultado_fluxo_mvp') IS NOT NULL THEN
        RAISE EXCEPTION '007A1_PRECHECK_FAIL: fugates.projecao_resultado_fluxo_mvp ja existe';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_roles
        WHERE rolname = 'fugates_dev_app'
    ) THEN
        RAISE EXCEPTION '007A1_PRECHECK_FAIL: role fugates_dev_app ausente';
    END IF;
END
$precheck$;

CREATE TABLE fugates.projecao_resultado_fluxo_mvp
(
    integracao_id uuid NOT NULL,

    fluxo text NOT NULL,
    status_funcional text NOT NULL,
    etapa_funcional text NOT NULL,
    resultado_funcional text NULL,
    decisao_funcional_pendente text NULL,

    sales_order text NULL,
    delivery_document text NULL,
    purchase_order text NULL,
    material_document text NULL,
    material_document_year text NULL,

    protocolo_guardian text NULL,
    numero_ticket_guardian text NULL,

    erro_codigo text NULL,
    erro_categoria text NULL,
    erro_etapa text NULL,
    erro_reprocessavel boolean NULL,
    erro_resultado_indeterminado boolean NOT NULL DEFAULT false,

    criado_em timestamptz NOT NULL DEFAULT clock_timestamp(),
    atualizado_em timestamptz NOT NULL DEFAULT clock_timestamp(),

    CONSTRAINT pk_projecao_resultado_fluxo_mvp
        PRIMARY KEY (integracao_id),

    CONSTRAINT fk_projecao_resultado_fluxo_mvp_integracao
        FOREIGN KEY (integracao_id)
        REFERENCES fugates.integracao(id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_projecao_resultado_fluxo_mvp_fluxo_formato
        CHECK (
            fluxo ~ '^[A-Z][A-Z0-9_]*$'
        ),

    CONSTRAINT ck_projecao_resultado_fluxo_mvp_status_formato
        CHECK (
            status_funcional ~ '^[A-Z][A-Z0-9_]*$'
        ),

    CONSTRAINT ck_projecao_resultado_fluxo_mvp_etapa_formato
        CHECK (
            etapa_funcional ~ '^[A-Z][A-Z0-9_]*$'
        ),

    CONSTRAINT ck_projecao_resultado_fluxo_mvp_opcionais_nao_vazios
        CHECK (
            (resultado_funcional IS NULL OR length(btrim(resultado_funcional)) > 0)
            AND (decisao_funcional_pendente IS NULL OR length(btrim(decisao_funcional_pendente)) > 0)
            AND (sales_order IS NULL OR length(btrim(sales_order)) > 0)
            AND (delivery_document IS NULL OR length(btrim(delivery_document)) > 0)
            AND (purchase_order IS NULL OR length(btrim(purchase_order)) > 0)
            AND (material_document IS NULL OR length(btrim(material_document)) > 0)
            AND (material_document_year IS NULL OR length(btrim(material_document_year)) > 0)
            AND (protocolo_guardian IS NULL OR length(btrim(protocolo_guardian)) > 0)
            AND (numero_ticket_guardian IS NULL OR length(btrim(numero_ticket_guardian)) > 0)
            AND (erro_codigo IS NULL OR length(btrim(erro_codigo)) > 0)
            AND (erro_categoria IS NULL OR length(btrim(erro_categoria)) > 0)
            AND (erro_etapa IS NULL OR length(btrim(erro_etapa)) > 0)
        ),

    CONSTRAINT ck_projecao_resultado_fluxo_mvp_decisao_pendente
        CHECK (
            status_funcional <> 'AGUARDANDO_DECISAO_FUNCIONAL'
            OR decisao_funcional_pendente IS NOT NULL
        ),

    CONSTRAINT ck_projecao_resultado_fluxo_mvp_indeterminado
        CHECK (
            erro_resultado_indeterminado =
            (status_funcional = 'RESULTADO_INDETERMINADO')
        )
);

CREATE INDEX ix_projecao_resultado_fluxo_mvp_protocolo_guardian
    ON fugates.projecao_resultado_fluxo_mvp (protocolo_guardian)
    WHERE protocolo_guardian IS NOT NULL;

REVOKE ALL ON TABLE fugates.projecao_resultado_fluxo_mvp FROM PUBLIC;
REVOKE ALL ON TABLE fugates.projecao_resultado_fluxo_mvp FROM fugates_dev_app;
GRANT SELECT, INSERT, UPDATE
    ON TABLE fugates.projecao_resultado_fluxo_mvp
    TO fugates_dev_app;

COMMIT;
