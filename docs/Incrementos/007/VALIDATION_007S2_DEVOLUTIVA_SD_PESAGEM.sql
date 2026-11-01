-- 007S2 - conferencia apos a migracao.
select ordinal_position, column_name, data_type, is_nullable
from information_schema.columns
where table_schema='fugates' and table_name='devolutiva_sd_pesagem'
order by ordinal_position;

select count(*) as column_count from information_schema.columns
where table_schema='fugates' and table_name='devolutiva_sd_pesagem';
select 17 as expected_column_count;

select c.relname as index_name, i.indisunique as is_unique, pg_get_indexdef(i.indexrelid) as def
from pg_class t join pg_namespace n on n.oid=t.relnamespace
join pg_index i on i.indrelid=t.oid join pg_class c on c.oid=i.indexrelid
where n.nspname='fugates' and t.relname='devolutiva_sd_pesagem' order by c.relname;

select tableowner as dono from pg_tables
where schemaname='fugates' and tablename='devolutiva_sd_pesagem';

select
  has_table_privilege('fugates_acm_hml_app','fugates.devolutiva_sd_pesagem','INSERT') as receptor_insert_ok,
  has_table_privilege('fugates_acm_hml_app','fugates.devolutiva_sd_pesagem','UPDATE') as receptor_update_ok,
  has_table_privilege('fugates_acm_hml_app','fugates.devolutiva_sd_pesagem','DELETE') as receptor_delete_deve_ser_false,
  has_table_privilege('fugates_sd_app','fugates.devolutiva_sd_pesagem','SELECT') as tela_select_ok,
  has_table_privilege('fugates_sd_app','fugates.devolutiva_sd_pesagem','INSERT') as tela_insert_deve_ser_false;

-- Prova funcional: o mesmo IdPesagem nao pode entrar duas vezes, e o estado tem de corresponder
-- a existencia da correlacao. Tudo desfeito ao final.
begin;
do $$
declare duplicou boolean := false; rotulo_solto boolean := false;
begin
    insert into fugates.devolutiva_sd_pesagem
        (id,id_pesagem,centro,tipo_pesagem,data_pesagem,peso_bruto,peso_tara,peso_liquido,
         chave_natural,ordem_venda,linha_ordem,estado,payload_json,recebido_em,atualizado_em)
    values (gen_random_uuid(),'9999999','3011','S',now(),20000,10000,9870.5,
            '782/10','782','10','CORRELACIONADA','{}',now(),now());
    begin
        insert into fugates.devolutiva_sd_pesagem
            (id,id_pesagem,centro,tipo_pesagem,data_pesagem,peso_bruto,peso_tara,peso_liquido,
             estado,payload_json,recebido_em,atualizado_em)
        values (gen_random_uuid(),'9999999','3011','S',now(),1,1,1,'SEM_CORRELACAO','{}',now(),now());
        duplicou := true;
    exception when unique_violation then duplicou := false;
    end;
    if duplicou then raise exception '007S2 FALHOU: o mesmo IdPesagem entrou duas vezes.'; end if;

    begin
        insert into fugates.devolutiva_sd_pesagem
            (id,id_pesagem,centro,tipo_pesagem,data_pesagem,peso_bruto,peso_tara,peso_liquido,
             estado,payload_json,recebido_em,atualizado_em)
        values (gen_random_uuid(),'9999998','3011','S',now(),1,1,1,'CORRELACIONADA','{}',now(),now());
        rotulo_solto := true;
    exception when check_violation then rotulo_solto := false;
    end;
    if rotulo_solto then raise exception '007S2 FALHOU: estado CORRELACIONADA aceito sem chave.'; end if;

    raise notice '007S2 OK: reenvio recusado e estado conferido contra a correlacao.';
end $$;
rollback;

select count(*) as linhas_deve_ser_zero from fugates.devolutiva_sd_pesagem;
