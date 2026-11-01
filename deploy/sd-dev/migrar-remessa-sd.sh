#!/usr/bin/env bash
# Migracao 007S3 - remessas do fluxo SD no PostgreSQL da VM (cluster 5433).
# Uso: sudo ./migrar-remessa-sd.sh {conferir|ensaio|aplicar|validar|reverter}
set -euo pipefail
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
psql_rodar() { sudo -u postgres psql -p 5433 -d fugates_dev -w -v ON_ERROR_STOP=1 "$@"; }
escrever_sql() {
  cat > "$TMP/migracao.sql" <<'FIM_MIGRACAO'
-- 007S3 - Fluxo SD: remessas criadas no SAP a partir das pesagens devolvidas.
--
-- Por que esta tabela existe:
-- remessa criada no SAP nao se desfaz por aqui. Uma pesagem gera UMA remessa, e a unicidade de
-- id_pesagem e o que garante isso - mesma logica de 007S1 (protocolo) e 007S2 (devolutiva).
--
-- Guarda tambem a recusa: o SAP devolve a causa numa mensagem de severidade "info" dentro de
-- errordetails, e so o codigo do topo (a consequencia) nao ajuda ninguem a corrigir. Por isso
-- mensagens_sap e texto livre com a lista inteira, na ordem em que ele devolveu.
begin;

set role fugates_dev_owner;

create table fugates.remessa_sd (
 id uuid not null,
 -- Pesagem de origem. E a chave que impede a segunda remessa.
 id_pesagem text not null,
 chave_natural text not null,
 ordem_venda text not null,
 linha_ordem text not null,
 -- 60 a 67, conforme a planilha de fluxo. E ele que escolhe litro ou quilo.
 setor_atividade text not null,
 -- Exatamente o que foi enviado: "1"/"L" no biodiesel, peso/"KG" nos demais.
 quantidade text not null,
 unidade text not null,
 -- Devolvido pelo SAP. Nulo quando a criacao nao se concluiu.
 numero_remessa text null,
 estado text not null,
 http_status smallint not null,
 payload_sha256 text not null,
 mensagens_sap text null,
 criado_em timestamptz not null,
 atualizado_em timestamptz not null,
 constraint pk_remessa_sd primary key (id),
 -- Uma pesagem, uma remessa.
 constraint uq_remessa_sd_pesagem unique (id_pesagem),
 -- Um numero de remessa pertence a uma pesagem so.
 constraint uq_remessa_sd_numero unique (numero_remessa),
 constraint ck_remessa_sd_estado
   check (estado in ('CRIADA', 'RECUSADA', 'DESFECHO_DESCONHECIDO')),
 -- CRIADA exige o numero: "criada sem numero" seria uma remessa que nao sabemos localizar.
 constraint ck_remessa_sd_criada_tem_numero
   check (estado <> 'CRIADA' or numero_remessa is not null),
 constraint ck_remessa_sd_chave_consistente
   check (chave_natural = ordem_venda || '/' || linha_ordem),
 constraint ck_remessa_sd_ordem_normalizada
   check (ordem_venda ~ '^[1-9][0-9]*$' or ordem_venda = '0'),
 constraint ck_remessa_sd_linha_normalizada
   check (linha_ordem ~ '^[1-9][0-9]*$' or linha_ordem = '0'),
 constraint ck_remessa_sd_setor check (setor_atividade in ('60','61','62','63','64','65','66','67')),
 constraint ck_remessa_sd_unidade check (unidade in ('L', 'KG')),
 -- Biodiesel (63) sempre 1 litro; os demais sempre quilo. A regra da planilha, no banco.
 constraint ck_remessa_sd_fluxo
   check ((setor_atividade = '63' and unidade = 'L' and quantidade = '1')
       or (setor_atividade <> '63' and unidade = 'KG')),
 constraint ck_remessa_sd_sha256 check (payload_sha256 ~ '^[0-9A-F]{64}$')
);

create index ix_remessa_sd_criado_em on fugates.remessa_sd (criado_em desc);
create index ix_remessa_sd_chave on fugates.remessa_sd (chave_natural);

revoke all privileges on table fugates.remessa_sd from public;
grant select, insert, update on table fugates.remessa_sd to fugates_acm_hml_app;
grant select, insert, update on table fugates.remessa_sd to fugates_sd_app;

-- O setor vem da ordem no momento do protocolo. Guardar aqui evita uma consulta ao SAP depois,
-- e registra o que valia quando a decisao foi tomada. Nulavel: os protocolos ja criados nao o
-- tem, e inventar um valor para eles seria pior do que admitir a ausencia.
alter table fugates.protocolo_sd_ordem_venda add column setor_atividade text null;
alter table fugates.protocolo_sd_ordem_venda add constraint ck_protocolo_sd_setor
  check (setor_atividade is null or setor_atividade in ('60','61','62','63','64','65','66','67'));

reset role;

commit;
FIM_MIGRACAO
  cat > "$TMP/validacao.sql" <<'FIM_VALIDACAO'
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
FIM_VALIDACAO
  cat > "$TMP/reverter.sql" <<'FIM_REVERTER'
-- 007S3 - desfaz a tabela de remessas SD e a coluna de setor no protocolo.
--
-- ATENCAO: apaga o registro de quais pesagens JA VIRARAM remessa no SAP. As remessas continuam
-- la - o rollback nao as desfaz - e o fluxo passaria a criar remessa duplicada para a mesma
-- pesagem. Exporte antes:
--   \copy (select * from fugates.remessa_sd) to 'remessa_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
alter table fugates.protocolo_sd_ordem_venda drop constraint if exists ck_protocolo_sd_setor;
alter table fugates.protocolo_sd_ordem_venda drop column if exists setor_atividade;
drop table fugates.remessa_sd;
commit;
FIM_REVERTER
}
existe() { psql_rodar -tAc "select to_regclass('fugates.remessa_sd') is not null"; }
case "${1:-}" in
  conferir) psql_rodar -c "select to_regclass('fugates.remessa_sd') is not null as remessa_existe, to_regclass('fugates.devolutiva_sd_pesagem') is not null as devolutiva_existe, to_regclass('fugates.protocolo_sd_ordem_venda') is not null as protocolo_existe" ;;
  ensaio)
    escrever_sql; echo "== Ensaio: cria e DESFAZ. Nada fica."
    sed 's/^commit;$/rollback;/' "$TMP/migracao.sql" > "$TMP/ensaio.sql"
    psql_rodar < "$TMP/ensaio.sql"; echo "== Existe agora? $(existe)   (esperado: f)" ;;
  aplicar)
    if [ "$(existe)" = "t" ]; then echo "Ja existe. Nada a fazer."; exit 0; fi
    escrever_sql; psql_rodar < "$TMP/migracao.sql"
    echo "== Aplicada. Existe? $(existe)   (esperado: t)"; echo "   Rode agora: $0 validar" ;;
  validar) escrever_sql; psql_rodar < "$TMP/validacao.sql" ;;
  reverter)
    echo "ATENCAO: apaga o registro de quais pesagens ja viraram remessa no SAP."
    printf 'Digite REVERTER para confirmar: '; read -r r
    [ "$r" = "REVERTER" ] || { echo "Cancelado."; exit 1; }
    psql_rodar -c "\copy (select * from fugates.remessa_sd) to '/tmp/remessa_sd_backup.csv' csv header"
    echo "Backup em /tmp/remessa_sd_backup.csv"; escrever_sql; psql_rodar < "$TMP/reverter.sql" ;;
  *) echo "Uso: sudo $0 {conferir|ensaio|aplicar|validar|reverter}"; exit 1 ;;
esac
