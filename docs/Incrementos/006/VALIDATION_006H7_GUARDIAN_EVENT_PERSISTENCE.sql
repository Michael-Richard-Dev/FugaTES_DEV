-- 006H7 R2 validation: strictly read-only.
BEGIN TRANSACTION READ ONLY;
SET LOCAL search_path TO fugates, pg_catalog;

DO $validation$
DECLARE
    v_count integer;
    v_text text;
    v_bool boolean;
BEGIN
    IF to_regclass('fugates.correlacao_guardian_evento') IS NULL THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: ledger ausente';
    END IF;

    SELECT count(*) INTO v_count
    FROM information_schema.columns
    WHERE table_schema = 'fugates'
      AND table_name = 'correlacao_guardian_evento';
    IF v_count <> 10 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: total colunas %, esperado 10', v_count;
    END IF;

    SELECT count(*) INTO v_count
    FROM (
        VALUES
            ('id', 'uuid', 'NO'),
            ('correlacao_id', 'uuid', 'NO'),
            ('sequencial_guardian', 'bigint', 'NO'),
            ('estado_codigo', 'integer', 'NO'),
            ('callback_fingerprint', 'text', 'NO'),
            ('peso_bruto', 'numeric', 'YES'),
            ('tara_final', 'numeric', 'YES'),
            ('peso_liquido', 'numeric', 'YES'),
            ('integracao_id', 'uuid', 'YES'),
            ('recebido_em', 'timestamp with time zone', 'NO')
    ) AS e(column_name, data_type, is_nullable)
    JOIN information_schema.columns c
      ON c.table_schema = 'fugates'
     AND c.table_name = 'correlacao_guardian_evento'
     AND c.column_name = e.column_name
     AND c.data_type = e.data_type
     AND c.is_nullable = e.is_nullable;
    IF v_count <> 10 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: nomes/tipos/nullability divergentes';
    END IF;

    SELECT column_default INTO v_text
    FROM information_schema.columns
    WHERE table_schema = 'fugates'
      AND table_name = 'correlacao_guardian_evento'
      AND column_name = 'recebido_em';
    IF v_text IS NULL OR regexp_replace(v_text, '[[:space:]]+', '', 'g') <> 'clock_timestamp()' THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: default recebido_em divergente: %', v_text;
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'fugates'
          AND table_name = 'correlacao_guardian_evento'
          AND column_name = 'protocolo_cloud'
    ) THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: protocolo_cloud proibido';
    END IF;

    SELECT count(*) INTO v_count
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
      AND c.contype = 'p'
      AND (
          SELECT array_agg(a.attname ORDER BY u.ord)
          FROM unnest(c.conkey) WITH ORDINALITY u(attnum, ord)
          JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = u.attnum
      ) = ARRAY['id']::name[];
    IF v_count <> 1 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: PK(id) divergente';
    END IF;

    SELECT count(*) INTO v_count
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
      AND c.contype = 'u'
      AND (
          SELECT array_agg(a.attname ORDER BY u.ord)
          FROM unnest(c.conkey) WITH ORDINALITY u(attnum, ord)
          JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = u.attnum
      ) = ARRAY['correlacao_id','sequencial_guardian','estado_codigo']::name[];
    IF v_count <> 1 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: business unique divergente';
    END IF;

    SELECT count(*) INTO v_count
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
      AND c.contype = 'f';
    IF v_count <> 2 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: total FKs %, esperado 2', v_count;
    END IF;

    SELECT count(*) INTO v_count
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
      AND c.contype = 'f'
      AND c.confrelid = 'fugates.correlacao_guardian_sap'::regclass
      AND c.confdeltype = 'r'
      AND NOT c.condeferrable
      AND (
          SELECT array_agg(a.attname ORDER BY u.ord)
          FROM unnest(c.conkey) WITH ORDINALITY u(attnum, ord)
          JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = u.attnum
      ) = ARRAY['correlacao_id']::name[];
    IF v_count <> 1 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: FK parent divergente';
    END IF;

    SELECT count(*) INTO v_count
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
      AND c.contype = 'f'
      AND c.confrelid = 'fugates.integracao'::regclass
      AND c.confdeltype = 'r'
      AND c.condeferrable
      AND c.condeferred
      AND (
          SELECT array_agg(a.attname ORDER BY u.ord)
          FROM unnest(c.conkey) WITH ORDINALITY u(attnum, ord)
          JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = u.attnum
      ) = ARRAY['integracao_id']::name[];
    IF v_count <> 1 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: FK integracao/deferred divergente';
    END IF;

    SELECT count(*) INTO v_count
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
      AND c.contype = 'c';
    IF v_count <> 3 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: total checks %, esperado 3', v_count;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint c
        WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
          AND c.contype = 'c'
          AND regexp_replace(pg_get_constraintdef(c.oid), '[[:space:]]+', '', 'g')
              LIKE '%estado_codigo=ANY(ARRAY[1,3,5])%'
    ) THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: check evento ausente/divergente';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint c
        WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) LIKE '%length(callback_fingerprint) = 64%'
          AND pg_get_constraintdef(c.oid) LIKE '%^[0-9A-F]{64}$%'
    ) THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: check fingerprint ausente/divergente';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint c
        WHERE c.conrelid = 'fugates.correlacao_guardian_evento'::regclass
          AND c.contype = 'c'
          AND pg_get_constraintdef(c.oid) LIKE '%integracao_id IS NULL%'
          AND pg_get_constraintdef(c.oid) LIKE '%peso_bruto IS NULL%'
          AND pg_get_constraintdef(c.oid) LIKE '%tara_final IS NULL%'
          AND pg_get_constraintdef(c.oid) LIKE '%peso_liquido IS NULL%'
          AND pg_get_constraintdef(c.oid) LIKE '%integracao_id IS NOT NULL%'
    ) THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: check semantico ausente/divergente';
    END IF;

    SELECT count(*) INTO v_count
    FROM pg_index i
    JOIN pg_class idx ON idx.oid = i.indexrelid
    WHERE i.indrelid = 'fugates.correlacao_guardian_evento'::regclass
      AND i.indisunique
      AND i.indpred IS NOT NULL
      AND pg_get_expr(i.indpred, i.indrelid) = '(integracao_id IS NOT NULL)'
      AND (
          SELECT array_agg(a.attname ORDER BY u.ord)
          FROM unnest(i.indkey::smallint[]) WITH ORDINALITY u(attnum, ord)
          JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = u.attnum
          WHERE u.attnum > 0
      ) = ARRAY['integracao_id']::name[];
    IF v_count <> 1 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: unique partial integracao_id divergente';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM information_schema.role_table_grants
        WHERE table_schema = 'fugates'
          AND table_name = 'correlacao_guardian_evento'
          AND grantee = 'PUBLIC'
    ) THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: PUBLIC possui privilegio proibido';
    END IF;

    IF NOT has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_evento', 'SELECT')
       OR NOT has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_evento', 'INSERT')
       OR has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_evento', 'UPDATE')
       OR has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_evento', 'DELETE')
       OR has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_evento', 'TRUNCATE') THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: ACL fugates_dev_app divergente';
    END IF;

    SELECT count(*) INTO v_count
    FROM information_schema.columns
    WHERE table_schema = 'fugates'
      AND table_name = 'correlacao_guardian_sap';
    IF v_count <> 26 THEN
        RAISE EXCEPTION '006H7_VALIDATION_FAIL: parent possui % colunas, esperado 26', v_count;
    END IF;

    FOREACH v_text IN ARRAY ARRAY[
        'protocolo_guardian',
        'ticket_guardian',
        'estado',
        'callback_fingerprint',
        'peso_bruto',
        'tara_final',
        'peso_liquido'
    ] LOOP
        IF NOT EXISTS (
            SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'fugates'
              AND table_name = 'correlacao_guardian_sap'
              AND column_name = v_text
        ) THEN
            RAISE EXCEPTION '006H7_VALIDATION_FAIL: coluna H5/H6 parent ausente: %', v_text;
        END IF;
    END LOOP;
END
$validation$;

SELECT '006H7_VALIDATION_R2=PASS' AS resultado;
ROLLBACK;
