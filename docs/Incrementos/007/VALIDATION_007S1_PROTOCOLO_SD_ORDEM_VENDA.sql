-- 007S1 - conferencia apos a migracao. Somente leitura: nao altera nada.

select
    ordinal_position,
    column_name,
    data_type,
    is_nullable
from information_schema.columns
where table_schema = 'fugates'
  and table_name = 'protocolo_sd_ordem_venda'
order by ordinal_position;

select count(*) as column_count
from information_schema.columns
where table_schema = 'fugates'
  and table_name = 'protocolo_sd_ordem_venda';

select 15 as expected_column_count;

select constraint_name, constraint_type
from information_schema.table_constraints
where table_schema = 'fugates'
  and table_name = 'protocolo_sd_ordem_venda'
order by constraint_name;

-- O bloqueio de duplicidade precisa estar apoiado em indice unico, nao so em intencao.
select
    index_class.relname as index_name,
    index_def.indisprimary as is_primary,
    index_def.indisunique as is_unique,
    pg_get_indexdef(index_def.indexrelid) as index_definition
from pg_catalog.pg_class table_class
join pg_catalog.pg_namespace table_namespace
  on table_namespace.oid = table_class.relnamespace
join pg_catalog.pg_index index_def
  on index_def.indrelid = table_class.oid
join pg_catalog.pg_class index_class
  on index_class.oid = index_def.indexrelid
where table_namespace.nspname = 'fugates'
  and table_class.relname = 'protocolo_sd_ordem_venda'
order by index_class.relname;

-- Privilegios: o aplicativo le, insere e atualiza. Nao apaga.
select
    has_table_privilege('fugates_dev_app', 'fugates.protocolo_sd_ordem_venda', 'SELECT') as select_granted,
    has_table_privilege('fugates_dev_app', 'fugates.protocolo_sd_ordem_venda', 'INSERT') as insert_granted,
    has_table_privilege('fugates_dev_app', 'fugates.protocolo_sd_ordem_venda', 'UPDATE') as update_granted,
    has_table_privilege('fugates_dev_app', 'fugates.protocolo_sd_ordem_venda', 'DELETE') as delete_granted_deve_ser_false,
    has_table_privilege('fugates_dev_app', 'fugates.protocolo_sd_ordem_venda', 'TRUNCATE') as truncate_granted_deve_ser_false;

select grantee, privilege_type
from information_schema.table_privileges
where table_schema = 'fugates'
  and table_name = 'protocolo_sd_ordem_venda'
  and grantee in ('fugates_dev_app', 'PUBLIC')
order by grantee, privilege_type;

-- Prova funcional do bloqueio, sem deixar rastro: a segunda insercao da mesma chave tem de
-- falhar. A transacao explicita em volta garante que nada fica na tabela, inclusive se o bloco
-- interromper por erro.
begin;

do $$
declare
    duplicou boolean := false;
begin
    insert into fugates.protocolo_sd_ordem_venda
        (id, chave_natural, ordem_venda, linha_ordem, centro, codigo_item_guardian,
         codigo_emissor_guardian, protocolo_id, estado, http_status, payload_sha256,
         url_retorno, criado_em, atualizado_em)
    values (gen_random_uuid(), '999999999/10', '999999999', '10', '3011', '7000000',
            '20000184', 999999999, 'CRIADO', 200, repeat('A', 64),
            'https://exemplo.invalid/retorno', now(), now());
    begin
        insert into fugates.protocolo_sd_ordem_venda
            (id, chave_natural, ordem_venda, linha_ordem, centro, codigo_item_guardian,
             codigo_emissor_guardian, protocolo_id, estado, http_status, payload_sha256,
             url_retorno, criado_em, atualizado_em)
        values (gen_random_uuid(), '999999999/10', '999999999', '10', '3011', '7000000',
                '20000184', 999999998, 'CRIADO', 200, repeat('B', 64),
                'https://exemplo.invalid/retorno', now(), now());
        duplicou := true;
    exception when unique_violation then
        duplicou := false;
    end;
    if duplicou then
        raise exception '007S1 FALHOU: a chave natural aceitou duplicata.';
    end if;
    raise notice '007S1 OK: chave natural recusou a duplicata.';
end $$;

rollback;

-- A tabela tem de terminar esta conferencia vazia.
select count(*) as linhas_deve_ser_zero from fugates.protocolo_sd_ordem_venda;
