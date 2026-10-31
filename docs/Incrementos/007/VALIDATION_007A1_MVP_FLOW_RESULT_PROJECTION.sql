BEGIN;

SET TRANSACTION READ ONLY;
SET LOCAL search_path TO fugates, pg_catalog;

DO $validation$
DECLARE
    v_owner text;
    v_count integer;
    v_text text;
    v_text_flat text;
    v_predicate text;
    v_rec record;
    v_projection_integracao_attnum smallint;
    v_parent_integracao_attnum smallint;
    v_protocol_attnum smallint;
    v_expected_check text;
    v_actual_check text;
    v_group text;
    v_depth integer;
    v_pos integer;
    v_whole boolean;
BEGIN
    IF to_regclass('fugates.projecao_resultado_fluxo_mvp') IS NULL THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: tabela ausente';
    END IF;

    SELECT pg_get_userbyid(c.relowner)
    INTO v_owner
    FROM pg_class c
    JOIN pg_namespace n
      ON n.oid = c.relnamespace
    WHERE n.nspname = 'fugates'
      AND c.relname = 'projecao_resultado_fluxo_mvp'
      AND c.relkind = 'r';

    IF v_owner IS DISTINCT FROM 'fugates_dev_owner' THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: owner divergente: %', v_owner;
    END IF;

    SELECT count(*)
    INTO v_count
    FROM information_schema.columns
    WHERE table_schema = 'fugates'
      AND table_name = 'projecao_resultado_fluxo_mvp';

    IF v_count <> 20 THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: column_count divergente: %', v_count;
    END IF;

    FOR v_rec IN
        SELECT *
        FROM (
            VALUES
                (1, 'integracao_id', 'uuid', 'NO'),
                (2, 'fluxo', 'text', 'NO'),
                (3, 'status_funcional', 'text', 'NO'),
                (4, 'etapa_funcional', 'text', 'NO'),
                (5, 'resultado_funcional', 'text', 'YES'),
                (6, 'decisao_funcional_pendente', 'text', 'YES'),
                (7, 'sales_order', 'text', 'YES'),
                (8, 'delivery_document', 'text', 'YES'),
                (9, 'purchase_order', 'text', 'YES'),
                (10, 'material_document', 'text', 'YES'),
                (11, 'material_document_year', 'text', 'YES'),
                (12, 'protocolo_guardian', 'text', 'YES'),
                (13, 'numero_ticket_guardian', 'text', 'YES'),
                (14, 'erro_codigo', 'text', 'YES'),
                (15, 'erro_categoria', 'text', 'YES'),
                (16, 'erro_etapa', 'text', 'YES'),
                (17, 'erro_reprocessavel', 'boolean', 'YES'),
                (18, 'erro_resultado_indeterminado', 'boolean', 'NO'),
                (19, 'criado_em', 'timestamp with time zone', 'NO'),
                (20, 'atualizado_em', 'timestamp with time zone', 'NO')
        ) AS expected(ordinal_position, column_name, data_type, is_nullable)
    LOOP
        IF NOT EXISTS (
            SELECT 1
            FROM information_schema.columns c
            WHERE c.table_schema = 'fugates'
              AND c.table_name = 'projecao_resultado_fluxo_mvp'
              AND c.ordinal_position = v_rec.ordinal_position
              AND c.column_name = v_rec.column_name
              AND c.data_type = v_rec.data_type
              AND c.is_nullable = v_rec.is_nullable
        ) THEN
            RAISE EXCEPTION
                '007A1_VALIDATION_FAIL: coluna divergente: ord=% name=% type=% nullable=%',
                v_rec.ordinal_position,
                v_rec.column_name,
                v_rec.data_type,
                v_rec.is_nullable;
        END IF;
    END LOOP;

    -- Every column without an authored default must prove column_default IS NULL.
    FOR v_rec IN
        SELECT column_name
        FROM (
            VALUES
                ('integracao_id'),
                ('fluxo'),
                ('status_funcional'),
                ('etapa_funcional'),
                ('resultado_funcional'),
                ('decisao_funcional_pendente'),
                ('sales_order'),
                ('delivery_document'),
                ('purchase_order'),
                ('material_document'),
                ('material_document_year'),
                ('protocolo_guardian'),
                ('numero_ticket_guardian'),
                ('erro_codigo'),
                ('erro_categoria'),
                ('erro_etapa'),
                ('erro_reprocessavel')
        ) AS x(column_name)
    LOOP
        SELECT column_default
        INTO v_text
        FROM information_schema.columns
        WHERE table_schema = 'fugates'
          AND table_name = 'projecao_resultado_fluxo_mvp'
          AND column_name = v_rec.column_name;

        IF v_text IS NOT NULL THEN
            RAISE EXCEPTION
                '007A1_VALIDATION_FAIL: default inesperado em %: %',
                v_rec.column_name,
                v_text;
        END IF;
    END LOOP;

    SELECT column_default
    INTO v_text
    FROM information_schema.columns
    WHERE table_schema = 'fugates'
      AND table_name = 'projecao_resultado_fluxo_mvp'
      AND column_name = 'erro_resultado_indeterminado';

    IF regexp_replace(COALESCE(v_text, ''), '[[:space:]]+', '', 'g') NOT IN ('false', 'false::boolean') THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: default erro_resultado_indeterminado divergente: %', v_text;
    END IF;

    FOR v_rec IN
        SELECT column_name
        FROM (VALUES ('criado_em'), ('atualizado_em')) AS x(column_name)
    LOOP
        SELECT column_default
        INTO v_text
        FROM information_schema.columns
        WHERE table_schema = 'fugates'
          AND table_name = 'projecao_resultado_fluxo_mvp'
          AND column_name = v_rec.column_name;

        IF regexp_replace(COALESCE(v_text, ''), '[[:space:]]+', '', 'g') <> 'clock_timestamp()' THEN
            RAISE EXCEPTION
                '007A1_VALIDATION_FAIL: default timestamp divergente em %: %',
                v_rec.column_name,
                v_text;
        END IF;
    END LOOP;

    SELECT a.attnum
    INTO v_projection_integracao_attnum
    FROM pg_attribute a
    WHERE a.attrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
      AND a.attname = 'integracao_id'
      AND a.attnum > 0
      AND NOT a.attisdropped;

    SELECT a.attnum
    INTO v_parent_integracao_attnum
    FROM pg_attribute a
    WHERE a.attrelid = 'fugates.integracao'::regclass
      AND a.attname = 'id'
      AND a.attnum > 0
      AND NOT a.attisdropped;

    SELECT a.attnum
    INTO v_protocol_attnum
    FROM pg_attribute a
    WHERE a.attrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
      AND a.attname = 'protocolo_guardian'
      AND a.attnum > 0
      AND NOT a.attisdropped;

    IF v_projection_integracao_attnum IS NULL
       OR v_parent_integracao_attnum IS NULL
       OR v_protocol_attnum IS NULL THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: attnum canonico ausente';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint c
        WHERE c.conrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
          AND c.conname = 'pk_projecao_resultado_fluxo_mvp'
          AND c.contype = 'p'
          AND c.convalidated
          AND c.conkey = ARRAY[v_projection_integracao_attnum]::smallint[]
          AND regexp_replace(pg_get_constraintdef(c.oid, true), '[[:space:]]+', '', 'g') =
              'PRIMARYKEY(integracao_id)'
    ) THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: PK ausente/divergente';
    END IF;

    SELECT count(*)
    INTO v_count
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
      AND c.contype = 'f';

    IF v_count <> 1 THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: FK count divergente: %', v_count;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint c
        WHERE c.conrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
          AND c.conname = 'fk_projecao_resultado_fluxo_mvp_integracao'
          AND c.contype = 'f'
          AND c.confrelid = 'fugates.integracao'::regclass
          AND c.conkey = ARRAY[v_projection_integracao_attnum]::smallint[]
          AND c.confkey = ARRAY[v_parent_integracao_attnum]::smallint[]
          AND c.confmatchtype = 's'
          AND c.confupdtype = 'a'
          AND c.confdeltype = 'r'
          AND NOT c.condeferrable
          AND NOT c.condeferred
          AND c.convalidated
    ) THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: FK integracao ausente/divergente';
    END IF;

    -- Cross-flow invariant: protocolo Guardian is projection data, never a FK.
    IF EXISTS (
        SELECT 1
        FROM pg_constraint c
        WHERE c.conrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
          AND c.contype = 'f'
          AND v_protocol_attnum = ANY(c.conkey)
    ) THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: protocolo_guardian possui FK indevida';
    END IF;

    SELECT count(*)
    INTO v_count
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
      AND c.contype = 'c';

    IF v_count <> 6 THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: CHECK count divergente: %', v_count;
    END IF;

    -- CHECK definitions are inspected from pg_get_constraintdef() and normalized.
    -- Formatting parentheses and implicit ::text casts are ignored; predicates are not.
    FOR v_rec IN
        SELECT *
        FROM (
            VALUES
                (
                    'ck_projecao_resultado_fluxo_mvp_fluxo_formato',
                    'CHECKfluxo~''^[A-Z][A-Z0-9_]*$'''
                ),
                (
                    'ck_projecao_resultado_fluxo_mvp_status_formato',
                    'CHECKstatus_funcional~''^[A-Z][A-Z0-9_]*$'''
                ),
                (
                    'ck_projecao_resultado_fluxo_mvp_etapa_formato',
                    'CHECKetapa_funcional~''^[A-Z][A-Z0-9_]*$'''
                ),
                (
                    'ck_projecao_resultado_fluxo_mvp_decisao_pendente',
                    'CHECKstatus_funcional<>''AGUARDANDO_DECISAO_FUNCIONAL''ORdecisao_funcional_pendenteISNOTNULL'
                ),
                (
                    'ck_projecao_resultado_fluxo_mvp_indeterminado',
                    'CHECKerro_resultado_indeterminado=status_funcional=''RESULTADO_INDETERMINADO'''
                )
        ) AS expected(conname, flat_definition)
    LOOP
        SELECT replace(
                   regexp_replace(pg_get_constraintdef(c.oid, true), '[[:space:]]+', '', 'g'),
                   '::text',
                   ''
               )
        INTO v_text
        FROM pg_constraint c
        WHERE c.conrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
          AND c.conname = v_rec.conname
          AND c.contype = 'c'
          AND c.convalidated;

        v_text_flat := replace(replace(v_text, '(', ''), ')', '');

        IF v_text_flat IS DISTINCT FROM v_rec.flat_definition THEN
            RAISE EXCEPTION
                '007A1_VALIDATION_FAIL: CHECK divergente %: actual=% expected=%',
                v_rec.conname,
                v_text,
                v_rec.flat_definition;
        END IF;
    END LOOP;

    -- R3 hardening: full canonical definition, sourced byte-for-byte from the R2 migration.
    -- Source constraint: ck_projecao_resultado_fluxo_mvp_opcionais_nao_vazios
    -- Canonical CHECK source SHA256: 5653DC18B469610FFDA9436A6B2BEB2A369AEAD55E7D5DF277F1C51200CDD29B
    v_expected_check := $canonical$
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
        )
$canonical$;

    SELECT pg_get_constraintdef(c.oid, true)
    INTO v_actual_check
    FROM pg_constraint c
    WHERE c.conrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
      AND c.conname = 'ck_projecao_resultado_fluxo_mvp_opcionais_nao_vazios'
      AND c.contype = 'c'
      AND c.convalidated;

    IF v_actual_check IS NULL THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: CHECK opcionais_nao_vazios ausente';
    END IF;

    -- Normalize only non-semantic deparser differences. Logical operators,
    -- operands, functions, columns, comparisons and extra expressions remain intact.
    v_expected_check := replace(
        replace(
            regexp_replace(v_expected_check, '[[:space:]]+', '', 'g'),
            '::text',
            ''
        ),
        '"',
        ''
    );
    v_actual_check := replace(
        replace(
            regexp_replace(v_actual_check, '[[:space:]]+', '', 'g'),
            '::text',
            ''
        ),
        '"',
        ''
    );

    IF left(v_expected_check, 6) <> 'CHECK('
       OR right(v_expected_check, 1) <> ')'
       OR left(v_actual_check, 6) <> 'CHECK('
       OR right(v_actual_check, 1) <> ')' THEN
        RAISE EXCEPTION
            '007A1_VALIDATION_FAIL: CHECK opcionais_nao_vazios envelope divergente: actual=% expected=%',
            v_actual_check,
            v_expected_check;
    END IF;

    -- Compare the CHECK bodies. Whole-expression wrapper parentheses may be
    -- emitted redundantly by PostgreSQL; strip only a pair proven to wrap
    -- the complete expression. Internal boolean grouping is preserved.
    v_expected_check := substr(v_expected_check, 7, char_length(v_expected_check) - 7);
    v_actual_check := substr(v_actual_check, 7, char_length(v_actual_check) - 7);

    FOR v_rec IN SELECT 1 AS pass FROM generate_series(1, 2)
    LOOP
        -- Expected side.
        LOOP
            EXIT WHEN char_length(v_expected_check) < 2
                OR left(v_expected_check, 1) <> '('
                OR right(v_expected_check, 1) <> ')';
            v_depth := 0;
            v_whole := true;
            FOR v_pos IN 1..char_length(v_expected_check)
            LOOP
                IF substr(v_expected_check, v_pos, 1) = '(' THEN
                    v_depth := v_depth + 1;
                ELSIF substr(v_expected_check, v_pos, 1) = ')' THEN
                    v_depth := v_depth - 1;
                    IF v_depth = 0 AND v_pos < char_length(v_expected_check) THEN
                        v_whole := false;
                        EXIT;
                    END IF;
                END IF;
                IF v_depth < 0 THEN
                    v_whole := false;
                    EXIT;
                END IF;
            END LOOP;
            EXIT WHEN NOT v_whole OR v_depth <> 0;
            v_expected_check := substr(v_expected_check, 2, char_length(v_expected_check) - 2);
        END LOOP;

        -- Actual side.
        LOOP
            EXIT WHEN char_length(v_actual_check) < 2
                OR left(v_actual_check, 1) <> '('
                OR right(v_actual_check, 1) <> ')';
            v_depth := 0;
            v_whole := true;
            FOR v_pos IN 1..char_length(v_actual_check)
            LOOP
                IF substr(v_actual_check, v_pos, 1) = '(' THEN
                    v_depth := v_depth + 1;
                ELSIF substr(v_actual_check, v_pos, 1) = ')' THEN
                    v_depth := v_depth - 1;
                    IF v_depth = 0 AND v_pos < char_length(v_actual_check) THEN
                        v_whole := false;
                        EXIT;
                    END IF;
                END IF;
                IF v_depth < 0 THEN
                    v_whole := false;
                    EXIT;
                END IF;
            END LOOP;
            EXIT WHEN NOT v_whole OR v_depth <> 0;
            v_actual_check := substr(v_actual_check, 2, char_length(v_actual_check) - 2);
        END LOOP;
    END LOOP;

    -- pg_get_constraintdef(pretty=true) may retain parentheses around atomic
    -- leaves. Remove only exact atomic wrappers from the 12 canonical groups.
    FOR v_rec IN
        SELECT column_name
        FROM (
            VALUES
                ('resultado_funcional'),
                ('decisao_funcional_pendente'),
                ('sales_order'),
                ('delivery_document'),
                ('purchase_order'),
                ('material_document'),
                ('material_document_year'),
                ('protocolo_guardian'),
                ('numero_ticket_guardian'),
                ('erro_codigo'),
                ('erro_categoria'),
                ('erro_etapa')
        ) AS x(column_name)
    LOOP
        v_expected_check := replace(
            v_expected_check,
            '(' || v_rec.column_name || 'ISNULL)',
            v_rec.column_name || 'ISNULL'
        );
        v_actual_check := replace(
            v_actual_check,
            '(' || v_rec.column_name || 'ISNULL)',
            v_rec.column_name || 'ISNULL'
        );

        v_expected_check := replace(
            v_expected_check,
            '(length(btrim(' || v_rec.column_name || '))>0)',
            'length(btrim(' || v_rec.column_name || '))>0'
        );
        v_actual_check := replace(
            v_actual_check,
            '(length(btrim(' || v_rec.column_name || '))>0)',
            'length(btrim(' || v_rec.column_name || '))>0'
        );

        v_group := '(' || v_rec.column_name ||
            'ISNULLORlength(btrim(' || v_rec.column_name || '))>0)';
        v_expected_check := replace(v_expected_check, '(' || v_group || ')', v_group);
        v_actual_check := replace(v_actual_check, '(' || v_group || ')', v_group);
    END LOOP;

    IF v_actual_check IS DISTINCT FROM v_expected_check THEN
        RAISE EXCEPTION
            '007A1_VALIDATION_FAIL: CHECK opcionais_nao_vazios full-definition divergente: actual=% expected=%',
            v_actual_check,
            v_expected_check;
    END IF;

    -- Exact protocol index semantics: named, btree, one key column, partial, non-unique.
    SELECT regexp_replace(
               pg_get_expr(i.indpred, i.indrelid, true),
               '[[:space:]]+',
               '',
               'g'
           )
    INTO v_predicate
    FROM pg_index i
    JOIN pg_class idx
      ON idx.oid = i.indexrelid
    JOIN pg_class tbl
      ON tbl.oid = i.indrelid
    JOIN pg_namespace n
      ON n.oid = tbl.relnamespace
    JOIN pg_am am
      ON am.oid = idx.relam
    WHERE n.nspname = 'fugates'
      AND tbl.relname = 'projecao_resultado_fluxo_mvp'
      AND idx.relname = 'ix_projecao_resultado_fluxo_mvp_protocolo_guardian'
      AND am.amname = 'btree'
      AND NOT i.indisunique
      AND NOT i.indisprimary
      AND i.indisvalid
      AND i.indisready
      AND i.indislive
      AND i.indnkeyatts = 1
      AND i.indnatts = 1
      AND i.indexprs IS NULL
      AND pg_get_indexdef(i.indexrelid, 1, true) = 'protocolo_guardian';

    IF v_predicate IS NULL
       OR v_predicate NOT IN (
           'protocolo_guardianISNOTNULL',
           '(protocolo_guardianISNOTNULL)'
       ) THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: indice protocolo Guardian ausente/divergente: %', v_predicate;
    END IF;

    -- No unique index may silently reintroduce global protocol uniqueness.
    SELECT count(*)
    INTO v_count
    FROM pg_index i
    WHERE i.indrelid = 'fugates.projecao_resultado_fluxo_mvp'::regclass
      AND i.indisunique
      AND EXISTS (
          SELECT 1
          FROM generate_series(1, i.indnkeyatts) AS key_position(position)
          WHERE pg_get_indexdef(i.indexrelid, key_position.position, true) = 'protocolo_guardian'
      );

    IF v_count <> 0 THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: protocolo_guardian possui indice UNIQUE indevido: %', v_count;
    END IF;

    IF NOT has_table_privilege(
        'fugates_dev_app',
        'fugates.projecao_resultado_fluxo_mvp',
        'SELECT'
    ) THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: app sem SELECT';
    END IF;

    IF NOT has_table_privilege(
        'fugates_dev_app',
        'fugates.projecao_resultado_fluxo_mvp',
        'INSERT'
    ) THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: app sem INSERT';
    END IF;

    IF NOT has_table_privilege(
        'fugates_dev_app',
        'fugates.projecao_resultado_fluxo_mvp',
        'UPDATE'
    ) THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: app sem UPDATE';
    END IF;

    IF has_table_privilege(
        'fugates_dev_app',
        'fugates.projecao_resultado_fluxo_mvp',
        'DELETE'
    )
    OR has_table_privilege(
        'fugates_dev_app',
        'fugates.projecao_resultado_fluxo_mvp',
        'TRUNCATE'
    )
    OR has_table_privilege(
        'fugates_dev_app',
        'fugates.projecao_resultado_fluxo_mvp',
        'REFERENCES'
    )
    OR has_table_privilege(
        'fugates_dev_app',
        'fugates.projecao_resultado_fluxo_mvp',
        'TRIGGER'
    ) THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: app possui privilegio excedente';
    END IF;

    SELECT count(*)
    INTO v_count
    FROM aclexplode(
        COALESCE(
            (
                SELECT c.relacl
                FROM pg_class c
                JOIN pg_namespace n
                  ON n.oid = c.relnamespace
                WHERE n.nspname = 'fugates'
                  AND c.relname = 'projecao_resultado_fluxo_mvp'
                  AND c.relkind = 'r'
            ),
            acldefault('r', 0)
        )
    ) a
    WHERE a.grantee = 0;

    IF v_count <> 0 THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: PUBLIC privileges encontrados: %', v_count;
    END IF;

    SELECT count(*)
    INTO v_count
    FROM fugates.projecao_resultado_fluxo_mvp p
    LEFT JOIN fugates.integracao i
      ON i.id = p.integracao_id
    WHERE i.id IS NULL;

    IF v_count <> 0 THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: orphan integracao: %', v_count;
    END IF;

    SELECT count(*)
    INTO v_count
    FROM (
        SELECT integracao_id
        FROM fugates.projecao_resultado_fluxo_mvp
        GROUP BY integracao_id
        HAVING count(*) > 1
    ) d;

    IF v_count <> 0 THEN
        RAISE EXCEPTION '007A1_VALIDATION_FAIL: duplicate integration projection: %', v_count;
    END IF;
END
$validation$;

SELECT '007A1_VALIDATION_R3=PASS' AS resultado;

ROLLBACK;
