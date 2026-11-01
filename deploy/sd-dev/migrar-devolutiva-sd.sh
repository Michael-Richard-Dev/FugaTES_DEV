#!/usr/bin/env bash
# Migracao 007S2 - devolutivas do Guardian no fluxo SD, no PostgreSQL da VM (cluster 5433).
# O SQL vai embutido: este arquivo e tudo o que precisa existir na VM.
# Copia de referencia em docs/Incrementos/007/ no repositorio.
#
# Uso: ./migrar-devolutiva-sd.sh {conferir|ensaio|aplicar|validar|reverter}
set -euo pipefail
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
psql_rodar() { sudo -u postgres psql -p 5433 -d fugates_dev -w -v ON_ERROR_STOP=1 "$@"; }
escrever_sql() {
  cat > "$TMP/migracao.sql" <<'FIM_MIGRACAO'
-- 007S2 - Fluxo SD: pesagens devolvidas pelo Guardian apos a balanca.
--
-- Por que esta tabela existe:
-- o Guardian pode reenviar o mesmo POST, e receber duas vezes nao pode virar duas remessas no
-- SAP. A unicidade de id_pesagem e o que impede isso - do mesmo jeito que a chave natural impede
-- protocolo duplicado em 007S1.
--
-- A correlacao com a ordem de venda vem dos campos adicionais 1003 (OrdemVendas) e 1009
-- (SD_ItDocV) e fica NULAVEL de proposito: devolutiva sem correlacao e guardada e sinalizada,
-- nunca descartada nem adivinhada. Correlacionar por aproximacao (parceiro, centro e data)
-- acerta no teste e erra em producao quando ha duas ordens do mesmo cliente no dia.
--
-- payload_json e TEXT, nao jsonb: e a evidencia do que chegou, byte a byte. jsonb reordena
-- chaves e descarta espacos, e ai deixa de ser o documento original.
begin;

set role fugates_dev_owner;

create table fugates.devolutiva_sd_pesagem (
 id uuid not null,
 -- Chave do evento no Guardian. E o que reconhece um reenvio.
 id_pesagem text not null,
 centro text not null,
 tipo_pesagem text not null,
 -- Sem fuso no wire: a hora local e preservada como veio, sem inventar UTC.
 data_pesagem timestamp not null,
 peso_bruto numeric(18,3) not null,
 peso_tara numeric(18,3) not null,
 -- Vem pronto da balanca, com o desconto de qualidade ja aplicado. Nao e bruto menos tara.
 peso_liquido numeric(18,3) not null,
 -- Correlacao, ja normalizada, no mesmo formato de protocolo_sd_ordem_venda.
 chave_natural text null,
 ordem_venda text null,
 linha_ordem text null,
 placa text null,
 nome_motorista text null,
 estado text not null,
 payload_json text not null,
 recebido_em timestamptz not null,
 atualizado_em timestamptz not null,
 constraint pk_devolutiva_sd_pesagem primary key (id),
 -- O bloqueio de reprocessamento.
 constraint uq_devolutiva_sd_pesagem_evento unique (id_pesagem),
 constraint ck_devolutiva_sd_pesagem_tipo check (tipo_pesagem = 'S'),
 constraint ck_devolutiva_sd_pesagem_estado
   check (estado in ('CORRELACIONADA', 'SEM_CORRELACAO')),
 -- Os tres campos da correlacao andam juntos: ou todos preenchidos, ou nenhum.
 constraint ck_devolutiva_sd_pesagem_correlacao_completa
   check ((chave_natural is null and ordem_venda is null and linha_ordem is null)
       or (chave_natural is not null and ordem_venda is not null and linha_ordem is not null)),
 -- O estado tem de corresponder a existencia da chave, e nao ser um rotulo solto.
 constraint ck_devolutiva_sd_pesagem_estado_confere
   check ((estado = 'CORRELACIONADA') = (chave_natural is not null)),
 constraint ck_devolutiva_sd_pesagem_chave_consistente
   check (chave_natural is null or chave_natural = ordem_venda || '/' || linha_ordem),
 constraint ck_devolutiva_sd_pesagem_ordem_normalizada
   check (ordem_venda is null or ordem_venda ~ '^[1-9][0-9]*$' or ordem_venda = '0'),
 constraint ck_devolutiva_sd_pesagem_linha_normalizada
   check (linha_ordem is null or linha_ordem ~ '^[1-9][0-9]*$' or linha_ordem = '0'),
 constraint ck_devolutiva_sd_pesagem_centro check (centro in ('3011', '3008')),
 constraint ck_devolutiva_sd_pesagem_pesos
   check (peso_bruto >= 0 and peso_tara >= 0 and peso_liquido >= 0)
);

-- A tela lista por chegada; sem isto a ordenacao varre a tabela inteira.
create index ix_devolutiva_sd_pesagem_recebido_em
  on fugates.devolutiva_sd_pesagem (recebido_em desc);

-- Procurar as devolutivas de uma linha de ordem e a consulta do fluxo seguinte (remessa).
create index ix_devolutiva_sd_pesagem_chave
  on fugates.devolutiva_sd_pesagem (chave_natural)
  where chave_natural is not null;

revoke all privileges on table fugates.devolutiva_sd_pesagem from public;

-- O receptor HTTP roda na VM como fugates_acm_hml_app: grava o que chega e atualiza a
-- correlacao. Sem DELETE nem TRUNCATE - devolutiva recebida e fato, nao rascunho.
grant select, insert, update on table fugates.devolutiva_sd_pesagem to fugates_acm_hml_app;

-- A tela do desktop so acompanha.
grant select on table fugates.devolutiva_sd_pesagem to fugates_sd_app;

reset role;

commit;
FIM_MIGRACAO
  cat > "$TMP/validacao.sql" <<'FIM_VALIDACAO'
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
FIM_VALIDACAO
  cat > "$TMP/reverter.sql" <<'FIM_REVERTER'
-- 007S2 - desfaz a tabela de devolutivas SD.
--
-- ATENCAO: apaga o registro do que o Guardian ja devolveu, inclusive a marca de quais pesagens
-- ja foram processadas. Com o fluxo ativo, um reenvio do Guardian passaria a ser tratado como
-- novo. Exporte antes:
--   \copy (select * from fugates.devolutiva_sd_pesagem) to 'devolutiva_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
drop table fugates.devolutiva_sd_pesagem;
commit;
FIM_REVERTER
}
existe() { psql_rodar -tAc "select to_regclass('fugates.devolutiva_sd_pesagem') is not null"; }
case "${1:-}" in
  conferir) psql_rodar -c "select current_user, to_regclass('fugates.devolutiva_sd_pesagem') is not null as tabela_existe, to_regclass('fugates.protocolo_sd_ordem_venda') is not null as protocolo_existe" ;;
  ensaio)
    escrever_sql; echo "== Ensaio: cria e DESFAZ. Nada fica."
    sed 's/^commit;$/rollback;/' "$TMP/migracao.sql" > "$TMP/ensaio.sql"
    psql_rodar < "$TMP/ensaio.sql"; echo "== Existe agora? $(existe)   (esperado: f)" ;;
  aplicar)
    if [ "$(existe)" = "t" ]; then echo "Ja existe. Nada a fazer."; exit 0; fi
    escrever_sql; psql_rodar < "$TMP/migracao.sql"
    echo "== Aplicada. Existe? $(existe)   (esperado: t)"
    psql_rodar -c "select tableowner as dono from pg_tables where schemaname='fugates' and tablename='devolutiva_sd_pesagem'"
    echo "   Rode agora: $0 validar" ;;
  validar) escrever_sql; psql_rodar < "$TMP/validacao.sql" ;;
  reverter)
    echo "ATENCAO: apaga o registro do que o Guardian ja devolveu, inclusive a marca de reenvio."
    printf 'Digite REVERTER para confirmar: '; read -r r
    [ "$r" = "REVERTER" ] || { echo "Cancelado."; exit 1; }
    psql_rodar -c "\copy (select * from fugates.devolutiva_sd_pesagem) to '/tmp/devolutiva_sd_backup.csv' csv header"
    echo "Backup em /tmp/devolutiva_sd_backup.csv"; escrever_sql; psql_rodar < "$TMP/reverter.sql" ;;
  *) echo "Uso: $0 {conferir|ensaio|aplicar|validar|reverter}"; exit 1 ;;
esac
