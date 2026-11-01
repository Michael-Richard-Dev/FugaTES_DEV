-- 007S3 - conferencia apos a migracao.
select ordinal_position, column_name, data_type, is_nullable
from information_schema.columns
where table_schema='fugates' and table_name='remessa_sd' order by ordinal_position;

select count(*) as column_count from information_schema.columns
where table_schema='fugates' and table_name='remessa_sd';
select 15 as expected_column_count;

select column_name, is_nullable from information_schema.columns
where table_schema='fugates' and table_name='protocolo_sd_ordem_venda' and column_name='setor_atividade';

select c.relname as index_name, i.indisunique as is_unique
from pg_class t join pg_namespace n on n.oid=t.relnamespace
join pg_index i on i.indrelid=t.oid join pg_class c on c.oid=i.indexrelid
where n.nspname='fugates' and t.relname='remessa_sd' order by c.relname;

select
  has_table_privilege('fugates_acm_hml_app','fugates.remessa_sd','INSERT') as receptor_insert_ok,
  has_table_privilege('fugates_sd_app','fugates.remessa_sd','INSERT') as tela_insert_ok,
  has_table_privilege('fugates_sd_app','fugates.remessa_sd','DELETE') as delete_deve_ser_false;

-- Prova funcional: uma pesagem so gera uma remessa, e a regra litro/quilo e do banco tambem.
begin;
do $$
declare duplicou boolean := false; fluxo_errado boolean := false;
begin
    insert into fugates.remessa_sd
      (id,id_pesagem,chave_natural,ordem_venda,linha_ordem,setor_atividade,quantidade,unidade,
       numero_remessa,estado,http_status,payload_sha256,criado_em,atualizado_em)
    values (gen_random_uuid(),'9999999','2985/10','2985','10','60','20100.000','KG',
            '80009999','CRIADA',201,repeat('A',64),now(),now());
    begin
        insert into fugates.remessa_sd
          (id,id_pesagem,chave_natural,ordem_venda,linha_ordem,setor_atividade,quantidade,unidade,
           estado,http_status,payload_sha256,criado_em,atualizado_em)
        values (gen_random_uuid(),'9999999','2985/10','2985','10','60','1.000','KG',
                'RECUSADA',400,repeat('B',64),now(),now());
        duplicou := true;
    exception when unique_violation then duplicou := false;
    end;
    if duplicou then raise exception '007S3 FALHOU: a mesma pesagem gerou duas remessas.'; end if;

    begin
        -- Biodiesel com quilo tem de ser recusado pela restricao de fluxo.
        insert into fugates.remessa_sd
          (id,id_pesagem,chave_natural,ordem_venda,linha_ordem,setor_atividade,quantidade,unidade,
           estado,http_status,payload_sha256,criado_em,atualizado_em)
        values (gen_random_uuid(),'9999998','1815/10','1815','10','63','20100.000','KG',
                'RECUSADA',400,repeat('C',64),now(),now());
        fluxo_errado := true;
    exception when check_violation then fluxo_errado := false;
    end;
    if fluxo_errado then raise exception '007S3 FALHOU: biodiesel aceito em quilo.'; end if;

    raise notice '007S3 OK: pesagem unica por remessa e regra litro/quilo conferidas.';
end $$;
rollback;

select count(*) as linhas_deve_ser_zero from fugates.remessa_sd;
