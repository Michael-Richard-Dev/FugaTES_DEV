select
    ordinal_position,
    column_name,
    data_type,
    is_nullable,
    column_default
from information_schema.columns
where table_schema = 'fugates'
  and table_name = 'correlacao_guardian_sap'
order by ordinal_position;

select count(*) as column_count
from information_schema.columns
where table_schema = 'fugates'
  and table_name = 'correlacao_guardian_sap';

select constraint_name, constraint_type
from information_schema.table_constraints
where table_schema = 'fugates'
  and table_name = 'correlacao_guardian_sap'
order by constraint_name;

select
    index_class.relname as index_name,
    index_def.indisprimary as is_primary,
    index_def.indisunique as is_unique,
    pg_get_indexdef(index_def.indexrelid) as index_definition,
    count(*) filter (where index_def.indisprimary) over () as pk_backing_index_count,
    count(*) filter (where index_def.indisunique and not index_def.indisprimary) over () as unique_backing_index_count,
    count(*) filter (
        where index_class.relname not in (
            'pk_correlacao_guardian_sap',
            'uq_correlacao_guardian_sap_documento',
            'uq_correlacao_guardian_sap_protocolo',
            'uq_correlacao_guardian_sap_ticket'
        )
    ) over () as extra_index_count
from pg_catalog.pg_class table_class
join pg_catalog.pg_namespace table_namespace
  on table_namespace.oid = table_class.relnamespace
join pg_catalog.pg_index index_def
  on index_def.indrelid = table_class.oid
join pg_catalog.pg_class index_class
  on index_class.oid = index_def.indexrelid
where table_namespace.nspname = 'fugates'
  and table_class.relname = 'correlacao_guardian_sap'
order by index_class.relname;

select count(distinct sequence_class.oid) as sequence_count
from pg_catalog.pg_class table_class
join pg_catalog.pg_namespace table_namespace
  on table_namespace.oid = table_class.relnamespace
join pg_catalog.pg_attribute column_def
  on column_def.attrelid = table_class.oid
 and column_def.attnum > 0
 and not column_def.attisdropped
left join pg_catalog.pg_attrdef default_def
  on default_def.adrelid = table_class.oid
 and default_def.adnum = column_def.attnum
left join pg_catalog.pg_depend dependency
  on dependency.classid = 'pg_attrdef'::regclass
 and dependency.objid = default_def.oid
 and dependency.refclassid = 'pg_class'::regclass
left join pg_catalog.pg_class sequence_class
  on sequence_class.oid = dependency.refobjid
 and sequence_class.relkind = 'S'
where table_namespace.nspname = 'fugates'
  and table_class.relname = 'correlacao_guardian_sap';

select
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'SELECT') as select_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'INSERT') as insert_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'UPDATE') as update_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'DELETE') as delete_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'TRUNCATE') as truncate_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'REFERENCES') as references_granted,
    has_table_privilege('fugates_dev_app', 'fugates.correlacao_guardian_sap', 'TRIGGER') as trigger_granted;

select grantee, privilege_type, is_grantable
from information_schema.table_privileges
where table_schema = 'fugates'
  and table_name = 'correlacao_guardian_sap'
  and grantee in ('fugates_dev_app', 'PUBLIC')
order by grantee, privilege_type;

select 20 as expected_column_count;
