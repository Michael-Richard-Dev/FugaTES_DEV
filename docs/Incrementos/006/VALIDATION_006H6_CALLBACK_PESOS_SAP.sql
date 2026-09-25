begin transaction read only;

with expected(name, type_name, nullable) as (
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
        ('atualizado_em', 'timestamp with time zone', 'NO'),
        ('protocolo_criado_em', 'timestamp with time zone', 'YES'),
        ('tipo_contrato', 'text', 'YES'),
        ('url_para_retorno', 'text', 'YES'),
        ('peso_bruto', 'numeric', 'YES'),
        ('tara_final', 'numeric', 'YES'),
        ('peso_liquido', 'numeric', 'YES')
), actual as (
    select column_name, data_type, is_nullable
    from information_schema.columns
    where table_schema = 'fugates' and table_name = 'correlacao_guardian_sap'
)
select
    (select count(*) from actual) = 26 as column_count_26,
    (select count(*) from expected e join actual a on a.column_name = e.name
        and a.data_type = e.type_name and a.is_nullable = e.nullable) = 26 as all_columns_match,
    (select count(*) from expected where name not in ('peso_bruto', 'tara_final', 'peso_liquido')) = 23
        as h5_columns_preserved,
    to_regclass('fugates.contrato_sap_toledo_identidade') is not null
        and to_regclass('fugates.contrato_sap_toledo_versao') is not null
        and (select count(*) from information_schema.columns where table_schema = 'fugates'
             and table_name = 'contrato_sap_toledo_identidade') = 3
        and (select count(*) from information_schema.columns where table_schema = 'fugates'
             and table_name = 'contrato_sap_toledo_versao') = 6 as h4_tables_intact,
    exists (select 1 from pg_trigger
        where tgrelid = 'fugates.integracao'::regclass
          and tgname = 'trg_h4_guard_claim_contrato_sap_toledo'
          and not tgisinternal) as h4_claim_guard_intact,
    not exists (select 1 from pg_class c,
        lateral aclexplode(coalesce(c.relacl, acldefault('r', c.relowner))) acl
        where c.oid = 'fugates.correlacao_guardian_sap'::regclass
          and acl.grantee = 0) as public_privileges_denied,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'SELECT') as select_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'INSERT') as insert_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'UPDATE') as update_granted,
    not has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'DELETE') as delete_denied,
    not has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'TRUNCATE') as truncate_denied,
    not has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'REFERENCES') as references_denied,
    not has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'TRIGGER') as trigger_denied;

rollback;
