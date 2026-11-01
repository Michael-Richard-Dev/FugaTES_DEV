#!/usr/bin/env bash
# Migracao 007S5 - passo 5 do fluxo SD: texto ZNFE da NF-e (cluster 5433).
# Uso: sudo ./migrar-texto-nfe-sd.sh {conferir|ensaio|aplicar|validar|reverter}
set -euo pipefail
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
psql_rodar() { sudo -u postgres psql -p 5433 -d fugates_dev -w -v ON_ERROR_STOP=1 "$@"; }

escrever_sql() {
  cat > "$TMP/migracao.sql" <<'FIM_MIGRACAO'
-- 007S5 - Fluxo SD, passo 5: texto de informacoes complementares da NF-e (ZNFE).
--
-- O texto nasce da ordem de venda e a balanca PREENCHE os espacos vazios dele. O PATCH no SAP
-- substitui o texto inteiro, entao o risco aqui nao e duplicar: e apagar o que veio do negocio.
--
-- Por isso texto_sha256 guarda a impressao digital do texto FINAL enviado. Com ela da para
-- provar depois que o que foi gravado e o que a tela mostrou na previa.
--
-- texto_criou diz se foi POST (nao havia texto) ou PATCH (havia). Sao caminhos diferentes no
-- SAP, e saber qual ocorreu muda o diagnostico quando algo sai errado.
begin;

set role fugates_dev_owner;

alter table fugates.remessa_sd add column texto_estado text null;
alter table fugates.remessa_sd add column texto_http_status smallint null;
alter table fugates.remessa_sd add column texto_mensagens text null;
alter table fugates.remessa_sd add column texto_sha256 text null;
alter table fugates.remessa_sd add column texto_criou boolean null;
alter table fugates.remessa_sd add column texto_em timestamptz null;

alter table fugates.remessa_sd add constraint ck_remessa_sd_texto_estado
  check (texto_estado is null
      or texto_estado in ('GRAVADO', 'RECUSADO', 'DESFECHO_DESCONHECIDO'));

alter table fugates.remessa_sd add constraint ck_remessa_sd_texto_exige_criada
  check (texto_estado is null or (estado = 'CRIADA' and numero_remessa is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_texto_completo
  check ((texto_estado is null and texto_em is null and texto_sha256 is null)
      or (texto_estado is not null and texto_em is not null and texto_sha256 is not null));

alter table fugates.remessa_sd add constraint ck_remessa_sd_texto_sha256
  check (texto_sha256 is null or texto_sha256 ~ '^[0-9A-F]{64}$');

reset role;

commit;
FIM_MIGRACAO

  cat > "$TMP/validacao.sql" <<'FIM_VALIDACAO'
\echo '== Colunas do texto (esperado: 6 linhas, todas nullable)'
select column_name, data_type, is_nullable
  from information_schema.columns
 where table_schema = 'fugates' and table_name = 'remessa_sd'
   and column_name like 'texto%'
 order by ordinal_position;

\echo '== Restricoes do texto (esperado: 4)'
select conname from pg_constraint
 where conrelid = 'fugates.remessa_sd'::regclass and conname like '%texto%'
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
  -- Texto exige remessa criada com numero.
  ok := false;
  begin
    execute base || ',texto_estado,texto_em,texto_sha256) values (gen_random_uuid(),'
      || '''T1'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',null,''RECUSADA'',400,'
      || quote_literal(sha) || ',null,now(),now(),''GRAVADO'',now(),' || quote_literal(sha) || ')';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S5 FALHOU: aceitou texto em remessa nao criada.'; end if;

  -- Estado sem hora nem sha.
  ok := false;
  begin
    execute base || ',texto_estado) values (gen_random_uuid(),'
      || '''T2'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',''80000011'',''CRIADA'',201,'
      || quote_literal(sha) || ',null,now(),now(),''GRAVADO'')';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S5 FALHOU: aceitou texto sem hora e sem sha.'; end if;

  -- Estado fora da lista.
  ok := false;
  begin
    execute base || ',texto_estado,texto_em,texto_sha256) values (gen_random_uuid(),'
      || '''T3'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',''80000012'',''CRIADA'',201,'
      || quote_literal(sha) || ',null,now(),now(),''QUASE'',now(),' || quote_literal(sha) || ')';
  exception when check_violation then ok := true;
  end;
  if not ok then raise exception '007S5 FALHOU: aceitou estado de texto fora da lista.'; end if;

  -- Caminho feliz, com texto_criou dizendo que foi PATCH.
  execute base || ',texto_estado,texto_em,texto_sha256,texto_criou) values (gen_random_uuid(),'
    || '''T4'',''1/10'',''1'',''10'',''60'',''1.000'',''KG'',''80000013'',''CRIADA'',201,'
    || quote_literal(sha) || ',null,now(),now(),''GRAVADO'',now(),' || quote_literal(sha) || ',false)';

  raise notice '007S5 OK: texto exige remessa criada, hora e sha, e estado da lista.';
end $$;
reset role;
rollback;

\echo '== Linhas apos a validacao (esperado: as que ja existiam)'
select count(*) as linhas, count(texto_estado) as com_texto from fugates.remessa_sd;
FIM_VALIDACAO

  cat > "$TMP/reverter.sql" <<'FIM_REVERTER'
-- ATENCAO: apaga o registro de QUAIS remessas ja tiveram o texto gravado. Os textos continuam
-- no SAP - o rollback nao os desfaz - e o fluxo poderia gravar de novo. Exporte antes:
--   \copy (select * from fugates.remessa_sd) to 'remessa_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_texto_sha256;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_texto_completo;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_texto_exige_criada;
alter table fugates.remessa_sd drop constraint if exists ck_remessa_sd_texto_estado;
alter table fugates.remessa_sd drop column if exists texto_em;
alter table fugates.remessa_sd drop column if exists texto_criou;
alter table fugates.remessa_sd drop column if exists texto_sha256;
alter table fugates.remessa_sd drop column if exists texto_mensagens;
alter table fugates.remessa_sd drop column if exists texto_http_status;
alter table fugates.remessa_sd drop column if exists texto_estado;
reset role;
commit;
FIM_REVERTER
}

existe() {
  psql_rodar -tAc "select exists (select 1 from information_schema.columns
    where table_schema='fugates' and table_name='remessa_sd' and column_name='texto_estado')"
}

case "${1:-}" in
  conferir)
    psql_rodar -c "select to_regclass('fugates.remessa_sd') is not null as remessa_existe"
    echo "== Colunas do texto ja existem? $(existe)" ;;
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
    echo "ATENCAO: apaga o registro de quais remessas ja tiveram o texto gravado no SAP."
    printf 'Digite REVERTER para confirmar: '; read -r r
    [ "$r" = "REVERTER" ] || { echo "Cancelado."; exit 1; }
    psql_rodar -c "\copy (select * from fugates.remessa_sd) to '/tmp/remessa_sd_backup.csv' csv header"
    echo "Backup em /tmp/remessa_sd_backup.csv"; escrever_sql; psql_rodar < "$TMP/reverter.sql" ;;
  *) echo "Uso: sudo $0 {conferir|ensaio|aplicar|validar|reverter}"; exit 1 ;;
esac
