#!/usr/bin/env bash
# Migracao 007S6 - passo 6 do fluxo SD: picking e saida de mercadoria (cluster 5433).
# Uso: sudo ./migrar-expedicao-sd.sh {conferir|ensaio|aplicar|validar|reverter}
set -euo pipefail
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
psql_rodar() { sudo -u postgres psql -p 5433 -d fugates_dev -w -v ON_ERROR_STOP=1 "$@"; }

escrever_sql() {
  cat > "$TMP/migracao.sql" <<'FIM_MIGRACAO'
-- 007S6 - Fluxo SD, passo 6: picking e saida de mercadoria.
--
-- Esta e a migracao que guarda o passo IRREVERSIVEL. A saida de mercadoria move estoque e gera
-- documento de material: nao existe "desfazer" por API neste fluxo. Por isso saida_estado, uma
-- vez gravado, impede uma segunda tentativa pela tela - e por isso ele e reservado ANTES do POST,
-- com DESFECHO_DESCONHECIDO, e nao depois.
--
-- Picking e separado da saida de proposito. Sao dois momentos, e um picking confirmado nao
-- autoriza sozinho a saida; quem autoriza e quem clica.
begin;

set role fugates_dev_owner;

alter table fugates.remessa_sd add column picking_estado text null;
alter table fugates.remessa_sd add column picking_http_status smallint null;
alter table fugates.remessa_sd add column picking_mensagens text null;
alter table fugates.remessa_sd add column picking_em timestamptz null;

alter table fugates.remessa_sd add column saida_estado text null;
alter table fugates.remessa_sd add column saida_http_status smallint null;
alter table fugates.remessa_sd add column saida_mensagens text null;
alter table fugates.remessa_sd add column saida_em timestamptz null;

alter table fugates.remessa_sd add constraint ck_remessa_sd_picking_estado
  check (picking_estado is null
      or picking_estado in ('CONFIRMADO', 'RECUSADO', 'DESFECHO_DESCONHECIDO'));

alter table fugates.remessa_sd add constraint ck_remessa_sd_saida_estado
  check (saida_estado is null
      or saida_estado in ('POSTADA', 'RECUSADA', 'DESFECHO_DESCONHECIDO'));

alter table fugates.remessa_sd add constraint ck_remessa_sd_picking_exige_criada
  check (picking_estado is null or (estado = 'CRIADA' and numero_remessa is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_saida_exige_criada
  check (saida_estado is null or (estado = 'CRIADA' and numero_remessa is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_picking_completo
  check ((picking_estado is null) = (picking_em is null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_saida_completo
  check ((saida_estado is null) = (saida_em is null));

reset role;

commit;
FIM_MIGRACAO

  cat > "$TMP/validacao.sql" <<'FIM_VALIDACAO'
\echo '== Colunas do passo 6 (esperado: 8 linhas, todas nullable)'
select column_name, data_type, is_nullable
  from information_schema.columns
 where table_schema = 'fugates' and table_name = 'remessa_sd'
   and (column_name like 'picking%' or column_name like 'saida%')
 order by ordinal_position;

\echo '== Restricoes do passo 6 (esperado: 6)'
select conname from pg_constraint
 where conrelid = 'fugates.remessa_sd'::regclass
   and (conname like '%picking%' or conname like '%saida%')
 order by conname;

\echo '== Permissao: a tela precisa de update; ninguem apaga'
select has_table_privilege('fugates_sd_app', 'fugates.remessa_sd', 'update') as tela_update_ok,
       has_table_privilege('fugates_sd_app', 'fugates.remessa_sd', 'delete') as delete_deve_ser_false;

\echo '== As regras recusam o que tem de recusar (nada fica: rollback ao fim)'
begin;
set role fugates_dev_owner;
do $$
declare
  base constant text := 'insert into fugates.remessa_sd (id,id_pesagem,chave_natural,ordem_venda,'
    || 'linha_ordem,setor_atividade,quantidade,unidade,numero_remessa,estado,http_status,'
    || 'payload_sha256,mensagens_sap,criado_em,atualizado_em';
  sha constant text := repeat('A', 64);
  ok boolean;
begin
  -- Saida exige remessa criada com numero.
  ok := false;
  begin
    execute base || ',saida_estado,saida_em) values (gen_random_uuid(),'
      || '''E1'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',null,''RECUSADA'',400,'
      || quote_literal(sha) || ',null,now(),now(),''POSTADA'',now())';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S6 FALHOU: aceitou saida em remessa nao criada.'; end if;

  -- Estado sem a hora: registro que nao responde "quando saiu".
  ok := false;
  begin
    execute base || ',saida_estado) values (gen_random_uuid(),'
      || '''E2'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',''80000021'',''CRIADA'',201,'
      || quote_literal(sha) || ',null,now(),now(),''POSTADA'')';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S6 FALHOU: aceitou saida sem hora.'; end if;

  -- Estado de picking fora da lista.
  ok := false;
  begin
    execute base || ',picking_estado,picking_em) values (gen_random_uuid(),'
      || '''E3'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',''80000022'',''CRIADA'',201,'
      || quote_literal(sha) || ',null,now(),now(),''MAIS_OU_MENOS'',now())';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S6 FALHOU: aceitou estado de picking fora da lista.'; end if;

  -- Caminho feliz: picking confirmado e saida postada.
  execute base || ',picking_estado,picking_em,saida_estado,saida_em) values (gen_random_uuid(),'
    || '''E4'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',''80000023'',''CRIADA'',201,'
    || quote_literal(sha) || ',null,now(),now(),''CONFIRMADO'',now(),''POSTADA'',now())';

  raise notice '007S6 OK: picking e saida exigem remessa criada, hora, e estado da lista.';
end $$;
reset role;
rollback;

\echo '== Linhas apos a validacao (esperado: as que ja existiam)'
select count(*) as linhas, count(saida_estado) as com_saida from fugates.remessa_sd;
FIM_VALIDACAO

  cat > "$TMP/reverter.sql" <<'FIM_REVERTER'
-- ATENCAO: apaga o registro de QUAIS remessas ja tiveram saida de mercadoria. O estoque
-- movimentado NAO volta - e sem este registro a tela deixaria postar a saida de novo.
-- Exporte antes:
--   \copy (select * from fugates.remessa_sd) to 'remessa_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_saida_completo;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_picking_completo;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_saida_exige_criada;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_picking_exige_criada;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_saida_estado;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_picking_estado;
alter table fugates.remessa_sd drop column if exists saida_em;
alter table fugates.remessa_sd drop column if exists saida_mensagens;
alter table fugates.remessa_sd drop column if exists saida_http_status;
alter table fugates.remessa_sd drop column if exists saida_estado;
alter table fugates.remessa_sd drop column if exists picking_em;
alter table fugates.remessa_sd drop column if exists picking_mensagens;
alter table fugates.remessa_sd drop column if exists picking_http_status;
alter table fugates.remessa_sd drop column if exists picking_estado;
reset role;
commit;
FIM_REVERTER
}

existe() {
  psql_rodar -tAc "select exists (select 1 from information_schema.columns
    where table_schema='fugates' and table_name='remessa_sd' and column_name='saida_estado')"
}

case "${1:-}" in
  conferir)
    psql_rodar -c "select to_regclass('fugates.remessa_sd') is not null as remessa_existe"
    echo "== Colunas do passo 6 ja existem? $(existe)" ;;
  ensaio)
    if [ "$(psql_rodar -tAc "select to_regclass('fugates.remessa_sd') is not null")" != "t" ]; then
      echo "A tabela fugates.remessa_sd nao existe. Rode antes a 007S3 (migrar-remessa-sd.sh)."; exit 1
    fi
    escrever_sql; echo "== Ensaio: altera e DESFAZ. Nada fica."
    sed 's/^commit;$/rollback;/' "$TMP/migracao.sql" > "$TMP/ensaio.sql"
    psql_rodar < "$TMP/ensaio.sql"; echo "== Existem agora? $(existe)   (esperado: f)" ;;
  aplicar)
    if [ "$(existe)" = "t" ]; then echo "Ja aplicada. Nada a fazer."; exit 0; fi
    escrever_sql; psql_rodar < "$TMP/migracao.sql"
    echo "== Aplicada. Existem? $(existe)   (esperado: t)"; echo "   Rode agora: $0 validar" ;;
  validar) escrever_sql; psql_rodar < "$TMP/validacao.sql" ;;
  reverter)
    echo "ATENCAO: apaga o registro de quais remessas ja tiveram saida de mercadoria."
    printf 'Digite REVERTER para confirmar: '; read -r r
    [ "$r" = "REVERTER" ] || { echo "Cancelado."; exit 1; }
    psql_rodar -c "\copy (select * from fugates.remessa_sd) to '/tmp/remessa_sd_backup.csv' csv header"
    echo "Backup em /tmp/remessa_sd_backup.csv"; escrever_sql; psql_rodar < "$TMP/reverter.sql" ;;
  *) echo "Uso: sudo $0 {conferir|ensaio|aplicar|validar|reverter}"; exit 1 ;;
esac
