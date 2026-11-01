#!/usr/bin/env bash
# Migracao 007S4 - passo 4 do fluxo SD: colunas do ajuste da remessa (cluster 5433).
# Uso: sudo ./migrar-ajuste-remessa-sd.sh {conferir|ensaio|aplicar|validar|reverter}
set -euo pipefail
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
psql_rodar() { sudo -u postgres psql -p 5433 -d fugates_dev -w -v ON_ERROR_STOP=1 "$@"; }

escrever_sql() {
  cat > "$TMP/migracao.sql" <<'FIM_MIGRACAO'
-- 007S4 - Fluxo SD, passo 4: ajuste da remessa depois de criada.
--
-- O passo 4 edita o cabecalho da remessa (meio de transporte sempre; pesos da balanca so no
-- biodiesel) e, no biodiesel, zera os pesos de todos os itens. Conforme "APIS SD para FUGATES".
--
-- Por que colunas e nao tabela nova:
-- o ajuste pertence a UMA remessa e acontece no maximo uma vez por ela. Uma tabela separada
-- permitiria duas linhas de ajuste para a mesma remessa, que e justamente o que nao pode.
--
-- Por que tudo nulavel:
-- as remessas criadas antes desta migracao nao passaram pelo passo 4. Nulo diz "nao ajustada";
-- preencher com um valor qualquer diria "ajustada", que seria falso.
begin;

set role fugates_dev_owner;

alter table fugates.remessa_sd add column ajuste_estado text null;
alter table fugates.remessa_sd add column ajuste_http_status smallint null;
alter table fugates.remessa_sd add column ajuste_mensagens text null;
-- Impressao digital do corpo enviado ao cabecalho. E o que permite conferir depois QUAL peso
-- foi escrito, sem guardar o documento inteiro.
alter table fugates.remessa_sd add column ajuste_payload_sha256 text null;
alter table fugates.remessa_sd add column ajuste_em timestamptz null;

alter table fugates.remessa_sd add constraint ck_remessa_sd_ajuste_estado
  check (ajuste_estado is null
      or ajuste_estado in ('AJUSTADA', 'RECUSADA', 'DESFECHO_DESCONHECIDO'));

-- So faz sentido ajustar o que foi criado: ajuste em remessa sem numero seria ajuste de nada.
alter table fugates.remessa_sd add constraint ck_remessa_sd_ajuste_exige_criada
  check (ajuste_estado is null or (estado = 'CRIADA' and numero_remessa is not null));

-- Desfecho registrado anda junto com a hora e o sha: um sem o outro deixaria o registro
-- incapaz de responder "quando" ou "o que foi escrito".
alter table fugates.remessa_sd add constraint ck_remessa_sd_ajuste_completo
  check ((ajuste_estado is null and ajuste_em is null and ajuste_payload_sha256 is null)
      or (ajuste_estado is not null and ajuste_em is not null and ajuste_payload_sha256 is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_ajuste_sha256
  check (ajuste_payload_sha256 is null or ajuste_payload_sha256 ~ '^[0-9A-F]{64}$');

reset role;

commit;
FIM_MIGRACAO

  cat > "$TMP/validacao.sql" <<'FIM_VALIDACAO'
\echo '== Colunas do ajuste (esperado: 5 linhas, todas nullable)'
select column_name, data_type, is_nullable
  from information_schema.columns
 where table_schema = 'fugates' and table_name = 'remessa_sd'
   and column_name like 'ajuste%'
 order by ordinal_position;

\echo '== Restricoes do ajuste (esperado: 4)'
select conname from pg_constraint
 where conrelid = 'fugates.remessa_sd'::regclass and conname like '%ajuste%'
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
  -- Ajuste exige remessa criada com numero: aqui a remessa foi RECUSADA.
  ok := false;
  begin
    execute base || ',ajuste_estado,ajuste_em,ajuste_payload_sha256) values (gen_random_uuid(),'
      || '''V1'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',null,''RECUSADA'',400,'
      || quote_literal(sha) || ',null,now(),now(),''AJUSTADA'',now(),' || quote_literal(sha) || ')';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S4 FALHOU: aceitou ajuste em remessa nao criada.'; end if;

  -- Estado de ajuste sem hora nem sha: registro que nao responde "quando" nem "o que".
  ok := false;
  begin
    execute base || ',ajuste_estado) values (gen_random_uuid(),'
      || '''V2'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',''80000001'',''CRIADA'',201,'
      || quote_literal(sha) || ',null,now(),now(),''AJUSTADA'')';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S4 FALHOU: aceitou ajuste sem hora e sem sha.'; end if;

  -- Estado fora da lista.
  ok := false;
  begin
    execute base || ',ajuste_estado,ajuste_em,ajuste_payload_sha256) values (gen_random_uuid(),'
      || '''V3'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',''80000002'',''CRIADA'',201,'
      || quote_literal(sha) || ',null,now(),now(),''TALVEZ'',now(),' || quote_literal(sha) || ')';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S4 FALHOU: aceitou estado de ajuste fora da lista.'; end if;

  -- O caminho feliz tem de passar.
  execute base || ',ajuste_estado,ajuste_em,ajuste_payload_sha256) values (gen_random_uuid(),'
    || '''V4'',''1/10'',''1'',''10'',''63'',''1'',''L'',''80000003'',''CRIADA'',201,'
    || quote_literal(sha) || ',null,now(),now(),''AJUSTADA'',now(),' || quote_literal(sha) || ')';

  raise notice '007S4 OK: ajuste exige remessa criada, hora e sha, e estado da lista.';
end $$;
reset role;
rollback;

\echo '== Linhas apos a validacao (esperado: as que ja existiam)'
select count(*) as linhas, count(ajuste_estado) as com_ajuste from fugates.remessa_sd;
FIM_VALIDACAO

  cat > "$TMP/reverter.sql" <<'FIM_REVERTER'
-- ATENCAO: apaga o registro de QUAIS remessas ja foram ajustadas no SAP. Os ajustes continuam
-- la - o rollback nao os desfaz - e o fluxo passaria a ajustar de novo a mesma remessa,
-- reescrevendo peso e transporte. Exporte antes:
--   \copy (select * from fugates.remessa_sd) to 'remessa_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_ajuste_sha256;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_ajuste_completo;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_ajuste_exige_criada;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_ajuste_estado;
alter table fugates.remessa_sd drop column if exists ajuste_em;
alter table fugates.remessa_sd drop column if exists ajuste_payload_sha256;
alter table fugates.remessa_sd drop column if exists ajuste_mensagens;
alter table fugates.remessa_sd drop column if exists ajuste_http_status;
alter table fugates.remessa_sd drop column if exists ajuste_estado;
reset role;
commit;
FIM_REVERTER
}

existe() {
  psql_rodar -tAc "select exists (select 1 from information_schema.columns
    where table_schema='fugates' and table_name='remessa_sd' and column_name='ajuste_estado')"
}

case "${1:-}" in
  conferir)
    psql_rodar -c "select to_regclass('fugates.remessa_sd') is not null as remessa_existe"
    echo "== Colunas do ajuste ja existem? $(existe)" ;;
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
    echo "ATENCAO: apaga o registro de quais remessas ja foram ajustadas no SAP."
    printf 'Digite REVERTER para confirmar: '; read -r r
    [ "$r" = "REVERTER" ] || { echo "Cancelado."; exit 1; }
    psql_rodar -c "\copy (select * from fugates.remessa_sd) to '/tmp/remessa_sd_backup.csv' csv header"
    echo "Backup em /tmp/remessa_sd_backup.csv"; escrever_sql; psql_rodar < "$TMP/reverter.sql" ;;
  *) echo "Uso: sudo $0 {conferir|ensaio|aplicar|validar|reverter}"; exit 1 ;;
esac
