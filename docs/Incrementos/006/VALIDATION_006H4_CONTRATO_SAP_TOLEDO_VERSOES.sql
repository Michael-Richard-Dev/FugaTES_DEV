-- FugaTES Incremento 006 - H4 Durable Idempotency
-- Gate: INC006-H4-DB-ARTIFACTS-R1
-- READ-ONLY validation. Do not execute in the artifact-construction gate.
--
-- External filesystem protection required before any future H4 DB apply:
-- F3_MIGRATION_EXPECTED_SHA256=E6273D7BB60866B334D6095ABC381CE7468CD05800905C5ACDD62661DF728329
-- F3_ROLLBACK_EXPECTED_SHA256=05D6B5BAEE860FC52A4DEC7712C01FC5E854F2B4B0CD47BECC19A30572F3BA60
-- F3_VALIDATION_EXPECTED_SHA256=1EECD9110EA30BEE8E57B4DBD7AD86E51754D04E8A13E8863D8939DF5C9B1D54
--
-- Baseline for this gate: F3 MUST already be applied and validated before H4 validation.
-- This validation therefore requires fugates.correlacao_guardian_sap to be present.
--
-- Registration transaction requirement:
-- identity lock + fingerprint dedup + version allocation + integracao INSERT
-- + version INSERT + ultima_versao UPDATE MUST share the SAME PostgreSQL
-- connection and SAME transaction. RegistrarCandidatoAsync must not run in a
-- separate transaction for H4 registration.

\pset pager off

begin;
set transaction read only;
set local search_path = pg_catalog, fugates;

with
function_info as (
    select
        p.oid,
        p.prorettype,
        p.prosecdef,
        p.proowner,
        p.proconfig,
        lower(
            regexp_replace(
                pg_catalog.pg_get_functiondef(p.oid),
                '[[:space:]]+',
                ' ',
                'g'
            )
        ) as def
    from pg_catalog.pg_proc p
    where p.oid = to_regprocedure(
        'fugates.fn_h4_guard_claim_contrato_sap_toledo()'
    )
),
trigger_info as (
    select
        t.tgtype,
        t.tgattr,
        t.tgfoid,
        t.tgenabled,
        a.attnum as estado_attnum
    from pg_catalog.pg_trigger t
    join pg_catalog.pg_attribute a
      on a.attrelid = t.tgrelid
     and a.attname = 'estado'
     and a.attnum > 0
     and not a.attisdropped
    where t.tgrelid = 'fugates.integracao'::regclass
      and t.tgname = 'trg_h4_guard_claim_contrato_sap_toledo'
      and not t.tgisinternal
),
inc005_expected(
    ordinal_position,
    column_name,
    udt_name,
    character_maximum_length,
    is_nullable
) as (
    values
        (1,  'id',                    'uuid',        null::integer, 'NO'),
        (2,  'origem',                'text',        null::integer, 'NO'),
        (3,  'destino',               'text',        null::integer, 'NO'),
        (4,  'operacao',              'text',        null::integer, 'NO'),
        (5,  'chave_negocio',         'text',        null::integer, 'NO'),
        (6,  'fingerprint_sha256',    'bpchar',      64,            'NO'),
        (7,  'estado',                'text',        null::integer, 'NO'),
        (8,  'tipo_disparo_pendente', 'text',        null::integer, 'YES'),
        (9,  'proxima_tentativa_em',  'timestamptz', null::integer, 'YES'),
        (10, 'fencing_token',         'int8',        null::integer, 'NO'),
        (11, 'worker_proprietario',   'text',        null::integer, 'YES'),
        (12, 'lease_expira_em',       'timestamptz', null::integer, 'YES'),
        (13, 'fase_envio',            'text',        null::integer, 'NO'),
        (14, 'tentativa_atual_id',    'uuid',        null::integer, 'YES'),
        (15, 'correlation_id',        'text',        null::integer, 'NO'),
        (16, 'criado_em',             'timestamptz', null::integer, 'NO'),
        (17, 'atualizado_em',         'timestamptz', null::integer, 'NO')
),
inc005_actual as (
    select
        ordinal_position,
        column_name,
        udt_name,
        character_maximum_length,
        is_nullable
    from information_schema.columns
    where table_schema = 'fugates'
      and table_name = 'integracao'
),
inc005_diff as (
    (select * from inc005_expected except select * from inc005_actual)
    union all
    (select * from inc005_actual except select * from inc005_expected)
),
checks(name, ok) as (
    values
    ('table_identidade_exists',
        to_regclass('fugates.contrato_sap_toledo_identidade') is not null),
    ('table_versao_exists',
        to_regclass('fugates.contrato_sap_toledo_versao') is not null),
    ('table_identidade_owner',
        coalesce((select pg_catalog.pg_get_userbyid(c.relowner)
                  from pg_catalog.pg_class c
                  where c.oid = to_regclass('fugates.contrato_sap_toledo_identidade')), '')
        = 'fugates_dev_owner'),
    ('table_versao_owner',
        coalesce((select pg_catalog.pg_get_userbyid(c.relowner)
                  from pg_catalog.pg_class c
                  where c.oid = to_regclass('fugates.contrato_sap_toledo_versao')), '')
        = 'fugates_dev_owner'),

    ('identidade_columns_exact',
        (select count(*) from information_schema.columns
         where table_schema='fugates'
           and table_name='contrato_sap_toledo_identidade') = 3
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_identidade'
              and column_name='numero_contrato'
              and data_type='text'
              and is_nullable='NO'
        )
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_identidade'
              and column_name='ultima_versao'
              and data_type='bigint'
              and is_nullable='NO'
              and regexp_replace(lower(coalesce(column_default,'')), '[[:space:]]', '', 'g')
                  in ('0','0::bigint')
        )
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_identidade'
              and column_name='criado_em'
              and data_type='timestamp with time zone'
              and is_nullable='NO'
              and regexp_replace(lower(coalesce(column_default,'')), '[[:space:]]', '', 'g')
                  = 'clock_timestamp()'
        )),

    ('versao_columns_exact',
        (select count(*) from information_schema.columns
         where table_schema='fugates'
           and table_name='contrato_sap_toledo_versao') = 6
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_versao'
              and column_name='numero_contrato'
              and data_type='text'
              and is_nullable='NO'
        )
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_versao'
              and column_name='versao'
              and data_type='bigint'
              and is_nullable='NO'
        )
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_versao'
              and column_name='fingerprint_sha256'
              and data_type='text'
              and udt_name='text'
              and is_nullable='NO'
        )
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_versao'
              and column_name='payload_contrato'
              and data_type='jsonb'
              and udt_name='jsonb'
              and is_nullable='NO'
        )
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_versao'
              and column_name='integracao_id'
              and data_type='uuid'
              and is_nullable='NO'
        )
        and exists (
            select 1 from information_schema.columns
            where table_schema='fugates'
              and table_name='contrato_sap_toledo_versao'
              and column_name='criado_em'
              and data_type='timestamp with time zone'
              and is_nullable='NO'
              and regexp_replace(lower(coalesce(column_default,'')), '[[:space:]]', '', 'g')
                  = 'clock_timestamp()'
        )),

    ('pk_identidade',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_identidade'::regclass
              and c.conname='pk_contrato_sap_toledo_identidade'
              and c.contype='p'
              and pg_catalog.pg_get_constraintdef(c.oid,true)
                  = 'PRIMARY KEY (numero_contrato)'
        )),
    ('pk_numero_versao',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_versao'::regclass
              and c.conname='pk_contrato_sap_toledo_versao'
              and c.contype='p'
              and pg_catalog.pg_get_constraintdef(c.oid,true)
                  = 'PRIMARY KEY (numero_contrato, versao)'
        )),
    ('unique_numero_fingerprint',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_versao'::regclass
              and c.conname='uq_contrato_sap_toledo_numero_fingerprint'
              and c.contype='u'
              and pg_catalog.pg_get_constraintdef(c.oid,true)
                  = 'UNIQUE (numero_contrato, fingerprint_sha256)'
        )),
    ('unique_integracao_id',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_versao'::regclass
              and c.conname='uq_contrato_sap_toledo_integracao_id'
              and c.contype='u'
              and pg_catalog.pg_get_constraintdef(c.oid,true)
                  = 'UNIQUE (integracao_id)'
        )),
    ('fk_numero_no_cascade',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_versao'::regclass
              and c.conname='fk_contrato_sap_toledo_versao_identidade'
              and c.contype='f'
              and c.confrelid='fugates.contrato_sap_toledo_identidade'::regclass
              and c.confdeltype='a'
        )),
    ('fk_integracao_no_cascade',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_versao'::regclass
              and c.conname='fk_contrato_sap_toledo_versao_integracao'
              and c.contype='f'
              and c.confrelid='fugates.integracao'::regclass
              and c.confdeltype='a'
        )),
    ('check_numero_nao_vazio',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_identidade'::regclass
              and c.conname='ck_contrato_sap_toledo_identidade_numero_nao_vazio'
              and c.contype='c'
              and lower(pg_catalog.pg_get_constraintdef(c.oid,true))
                  like '%length(numero_contrato) > 0%'
        )),
    ('check_ultima_versao',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_identidade'::regclass
              and c.conname='ck_contrato_sap_toledo_identidade_ultima_versao'
              and c.contype='c'
              and lower(pg_catalog.pg_get_constraintdef(c.oid,true))
                  like '%ultima_versao >= 0%'
        )),
    ('check_versao_positiva',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_versao'::regclass
              and c.conname='ck_contrato_sap_toledo_versao_positiva'
              and c.contype='c'
              and lower(pg_catalog.pg_get_constraintdef(c.oid,true))
                  like '%versao > 0%'
        )),
    ('check_fingerprint_64_upper_hex',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_versao'::regclass
              and c.conname='ck_contrato_sap_toledo_fingerprint_sha256'
              and c.contype='c'
              and lower(pg_catalog.pg_get_constraintdef(c.oid,true))
                  like '%length(fingerprint_sha256) = 64%'
              and pg_catalog.pg_get_constraintdef(c.oid,true)
                  like '%^[0-9A-F]{64}$%'
        )),
    ('check_payload_json_object',
        exists (
            select 1 from pg_catalog.pg_constraint c
            where c.conrelid='fugates.contrato_sap_toledo_versao'::regclass
              and c.conname='ck_contrato_sap_toledo_payload_object'
              and c.contype='c'
              and lower(pg_catalog.pg_get_constraintdef(c.oid,true))
                  like '%jsonb_typeof(payload_contrato) = ''object''%'
        )),

    ('guard_function_exists', (select count(*)=1 from function_info)),
    ('guard_returns_trigger',
        coalesce((select prorettype='trigger'::regtype from function_info),false)),
    ('guard_security_definer',
        coalesce((select prosecdef from function_info),false)),
    ('guard_owner',
        coalesce((select pg_catalog.pg_get_userbyid(proowner) from function_info),'')
        = 'fugates_dev_owner'),
    ('guard_hardened_search_path',
        coalesce((
            select exists (
                select 1
                from unnest(coalesce(proconfig,array[]::text[])) cfg
                where lower(cfg) like 'search_path=pg_catalog, fugates%'
            ) from function_info
        ),false)),
    ('guard_scope_transition_to_processing',
        coalesce((select def like '%old.estado is distinct from ''em_processamento''%'
                         and def like '%new.estado = ''em_processamento''%'
                  from function_info),false)),
    ('guard_non_h4_bypass',
        coalesce((select def like '%from fugates.contrato_sap_toledo_versao v%'
                         and def like '%if not found then return new;%'
                  from function_info),false)),
    ('guard_identity_for_update',
        coalesce((select def like '%from fugates.contrato_sap_toledo_identidade i%'
                         and def like '%for update%'
                  from function_info),false)),
    ('guard_blocks_pending',
        coalesce((select def like '%''pendente''%' from function_info),false)),
    ('guard_blocks_processing',
        coalesce((select def like '%''em_processamento''%' from function_info),false)),
    ('guard_blocks_reprocessing',
        coalesce((select def like '%''aguardando_reprocessamento''%' from function_info),false)),
    ('guard_blocks_manual_action',
        coalesce((select def like '%''aguardando_acao_manual''%' from function_info),false)),
    ('guard_blocks_indeterminate',
        coalesce((select def like '%''resultado_indeterminado''%' from function_info),false)),
    ('guard_suppresses_blocked_claim',
        coalesce((select def like '%return null;%' from function_info),false)),

    ('guard_trigger_exists_once', (select count(*)=1 from trigger_info)),
    ('guard_trigger_before_row_update',
        coalesce((select tgtype=19 from trigger_info),false)),
    ('guard_trigger_update_of_estado_only',
        coalesce((select tgattr::text=estado_attnum::text from trigger_info),false)),
    ('guard_trigger_function_exact',
        coalesce((select tgfoid=to_regprocedure(
                    'fugates.fn_h4_guard_claim_contrato_sap_toledo()'
                 ) from trigger_info),false)),
    ('guard_trigger_enabled',
        coalesce((select tgenabled in ('O','A') from trigger_info),false)),

    ('runtime_schema_usage',
        has_schema_privilege('fugates_dev_app','fugates','USAGE')),
    ('runtime_identidade_select',
        has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_identidade','SELECT')),
    ('runtime_identidade_insert',
        has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_identidade','INSERT')),
    ('runtime_identidade_update_only_ultima_versao',
        has_column_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_identidade','ultima_versao','UPDATE')
        and not has_column_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_identidade','numero_contrato','UPDATE')
        and not has_column_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_identidade','criado_em','UPDATE')),
    ('runtime_identidade_no_delete_truncate',
        not has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_identidade','DELETE')
        and not has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_identidade','TRUNCATE')),
    ('runtime_versao_select_insert',
        has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_versao','SELECT')
        and has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_versao','INSERT')),
    ('runtime_versao_no_update_delete_truncate',
        not has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_versao','UPDATE')
        and not has_any_column_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_versao','UPDATE')
        and not has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_versao','DELETE')
        and not has_table_privilege('fugates_dev_app',
            'fugates.contrato_sap_toledo_versao','TRUNCATE')),
    ('runtime_guard_execute',
        has_function_privilege('fugates_dev_app',
            'fugates.fn_h4_guard_claim_contrato_sap_toledo()','EXECUTE')),

    ('inc005_integracao_exact_17_columns',
        (select count(*) from inc005_actual)=17
        and not exists (select 1 from inc005_diff)),
    ('f3_applied',
        to_regclass('fugates.correlacao_guardian_sap') is not null)
)
select
    jsonb_object_agg(name, ok order by name) as validation_checks,
    case when bool_and(ok) then 'true' else 'false' end as validation_all_ok
from checks
\gset

\echo 'VALIDATION_CHECKS=' :validation_checks

\if :validation_all_ok
    \echo 'GAIA_INC006_H4_DB_VALIDATION=PASS'
    \echo 'TABLE_COUNT=2'
    \echo 'FINGERPRINT_TYPE=text'
    \echo 'PAYLOAD_TYPE=jsonb'
    \echo 'CLAIM_GUARD_CREATED=SIM'
    \echo 'NON_H4_BYPASS=SIM'
    \echo 'PRIOR_INDETERMINATE_BLOCKS_NEXT=SIM'
    \echo 'INC005_SCHEMA_COLUMNS_CHANGED=NAO'
    \echo 'F3_DATABASE_STATE_CHANGED=NAO'
    \echo 'REGISTRATION_SAME_CONNECTION_SAME_TRANSACTION=REQUIRED'
\else
    \echo 'GAIA_INC006_H4_DB_VALIDATION=FAIL'
    rollback;
    select 1/0;
\endif

rollback;

\echo 'VALIDATION_TRANSACTION_ROLLBACK=SUCCESS'
\echo 'DATABASE_WRITES=0'
