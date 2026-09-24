begin transaction read only;

with f3_columns(name, type_name, nullable) as (
    values
        ('id', 'uuid', 'NO'),
        ('documento_logistico_referencia', 'text', 'NO'),
        ('protocolo_guardian', 'text', 'YES'),
        ('ticket_guardian', 'text', 'YES'),
        ('material_sap', 'text', 'NO'),
        ('bp_emissor', 'text', 'NO'),
        ('bp_transportadora', 'text', 'NO'),
        ('centro_sap', 'text', 'NO'),
        ('tipo_documento', 'text', 'NO'),
        ('contrato_sap', 'text', 'NO'),
        ('operacao_contrato', 'text', 'NO'),
        ('tipo_operacao_sap', 'smallint', 'NO'),
        ('contrato_acm_sap', 'text', 'YES'),
        ('pedido_compras_sap', 'text', 'YES'),
        ('processo', 'text', 'NO'),
        ('direcao', 'text', 'NO'),
        ('estado', 'text', 'NO'),
        ('callback_fingerprint', 'text', 'YES'),
        ('criado_em', 'timestamp with time zone', 'NO'),
        ('atualizado_em', 'timestamp with time zone', 'NO')
), actual as (
    select column_name, data_type, is_nullable
    from information_schema.columns
    where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap'
)
select
    (select count(*) from actual) = 23 as column_count_23,
    (select count(*) from f3_columns c join actual a on a.column_name = c.name
        and a.data_type = c.type_name and a.is_nullable = c.nullable) = 20 as f3_columns_preserved,
    (select count(*) from actual a where a.column_name not in (select name from f3_columns)
        and a.column_name not in ('protocolo_criado_em', 'tipo_contrato', 'url_para_retorno')) = 0 as no_extra_columns,
    exists (select 1 from actual where column_name = 'protocolo_criado_em'
        and data_type = 'timestamp with time zone' and is_nullable = 'YES') as h5_column_type_and_nullability,
    exists (select 1 from actual where column_name = 'tipo_contrato'
        and data_type = 'text' and is_nullable = 'YES') as tipo_contrato_type_and_nullability,
    exists (select 1 from actual where column_name = 'url_para_retorno'
        and data_type = 'text' and is_nullable = 'YES') as url_para_retorno_type_and_nullability,
    to_regclass('fugates.contrato_sap_toledo_identidade') is not null
        and to_regclass('fugates.contrato_sap_toledo_versao') is not null
        and (select count(*) from information_schema.columns where table_schema = 'fugates'
             and table_name = 'contrato_sap_toledo_identidade') = 3
        and (select count(*) from information_schema.columns where table_schema = 'fugates'
             and table_name = 'contrato_sap_toledo_versao') = 6 as h4_tables_intact,
    exists (select 1 from pg_catalog.pg_trigger t
        where t.tgrelid = 'fugates.integracao'::regclass
          and t.tgname = 'trg_h4_guard_claim_contrato_sap_toledo' and not t.tgisinternal) as h4_trigger_present,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'SELECT') as select_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'INSERT') as insert_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'UPDATE') as update_granted,
    not has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'DELETE') as delete_denied,
    not has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'TRUNCATE') as truncate_denied,
    not has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'REFERENCES') as references_denied,
    not has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'TRIGGER') as trigger_denied;

rollback;
