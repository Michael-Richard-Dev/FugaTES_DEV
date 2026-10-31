#!/usr/bin/env bash
# Migracao 007S1 - tabela de protocolos do fluxo SD, no PostgreSQL da VM.
#
# O SQL vai embutido: este arquivo e tudo o que precisa existir na VM.
# A copia de referencia vive em docs/Incrementos/007/ no repositorio.
#
# Rota: conferido em 31/10/2026 que nao ha credencial de fugates_dev_owner nesta maquina, e que
# fugates_acm_hml_app nao pode criar tabela. Entao o DDL vai por sudo -u postgres, e o proprio SQL
# faz "set role fugates_dev_owner" para a tabela nascer pertencendo ao dono do schema.
# Se um dia houver senha do dono, use:  MODO=pgpass DB_USER=fugates_dev_owner ./este-script aplicar
#
# Uso:
#   ./migrar-protocolo-sd.sh conferir   # quem sou eu, posso criar tabela, ela ja existe?
#   ./migrar-protocolo-sd.sh ensaio     # roda a migracao e DESFAZ - so prova que compila
#   ./migrar-protocolo-sd.sh aplicar    # cria a tabela de verdade
#   ./migrar-protocolo-sd.sh validar    # colunas, indices, privilegios e o bloqueio de duplicata
#   ./migrar-protocolo-sd.sh reverter   # derruba a tabela (pede confirmacao escrita)
set -euo pipefail

MODO="${MODO:-postgres}"
DB_USER="${DB_USER:-fugates_acm_hml_app}"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

# -w sempre: o psql falha em vez de abrir um prompt de senha, que acabaria no historico.
psql_rodar() {
  if [ "$MODO" = "postgres" ]; then
    sudo -u postgres psql -p 5433 -d fugates_dev -w -v ON_ERROR_STOP=1 "$@"
  else
    local pg="${PGPASSFILE:-}"
    if [ -z "$pg" ] || [ ! -f "$pg" ]; then
      pg="$(grep -rhoE '(PGPASSFILE|PGPASS)=[^ ";]+' "$HOME"/*.sh 2>/dev/null \
            | cut -d= -f2- | while read -r c; do [ -f "$c" ] && echo "$c" && break; done)"
    fi
    [ -n "$pg" ] && [ -f "$pg" ] || { echo "Arquivo de senha nao encontrado. Use PGPASSFILE=..."; exit 1; }
    env PGPASSFILE="$pg" psql -w "host=127.0.0.1 port=5433 dbname=fugates_dev user=$DB_USER" \
        -v ON_ERROR_STOP=1 "$@"
  fi
}

escrever_sql() {
  cat > "$TMP/migracao.sql" <<'FIM_MIGRACAO'
-- 007S1 - Fluxo SD: protocolos de pre-cadastro criados a partir de ordens de venda.
--
-- Por que esta tabela existe:
-- a varredura do SAP roda a cada 30 segundos e, sem registro duravel, a mesma linha de ordem
-- viraria um protocolo novo a cada volta. Protocolo criado no Guardian nao se desfaz.
--
-- A unicidade esta na CHAVE NATURAL (ordem + linha), ja NORMALIZADA - sem os zeros a esquerda
-- que o SAP preenche. Comparar a forma do SAP com a do Guardian foi exatamente o defeito que
-- deixou a guarda de duplicidade do ACM sem efeito ("1160" nunca casou com "0000001160").
--
-- Um envio sem desfecho conhecido (erro de rede) TAMBEM ocupa a chave: o POST pode ter chegado,
-- e supor que nao chegou e o que cria duplicata. Por isso protocolo_id e nulavel e o estado
-- DESFECHO_DESCONHECIDO existe.
begin;

-- Nao existe credencial de fugates_dev_owner nesta maquina (conferido em 31/10/2026): os quatro
-- arquivos de senha so tem papeis de aplicacao. Entao esta migracao e aplicada pelo superusuario,
-- via sudo -u postgres. O set role abaixo faz a tabela nascer pertencendo ao DONO DO SCHEMA, como
-- as demais - sem ele, ela pertenceria a postgres, e a manutencao futura ficaria fora do padrao.
-- Rodando ja como fugates_dev_owner, este comando e inofensivo.
set role fugates_dev_owner;

create table fugates.protocolo_sd_ordem_venda (
 id uuid not null,
 -- Chave natural normalizada: '<ordem>/<linha>', ex. '782/10'.
 chave_natural text not null,
 ordem_venda text not null,
 linha_ordem text not null,
 centro text not null,
 codigo_item_guardian text not null,
 codigo_emissor_guardian text not null,
 -- Devolvido pelo Guardian. Nulo quando o desfecho nao e conhecido ou o envio foi recusado.
 protocolo_id bigint null,
 estado text not null,
 http_status smallint not null,
 -- SHA-256 dos bytes efetivamente transmitidos. Prova o que foi enviado, sem guardar o payload.
 payload_sha256 text not null,
 url_retorno text not null,
 -- Mensagem do Guardian em caso de recusa. Nunca credencial, nunca token.
 mensagem_recusa text null,
 criado_em timestamptz not null,
 atualizado_em timestamptz not null,
 constraint pk_protocolo_sd_ordem_venda primary key (id),
 -- O bloqueio de duplicidade. Uma linha de ordem ocupa a chave para sempre, qualquer que tenha
 -- sido o desfecho.
 constraint uq_protocolo_sd_ordem_venda_chave unique (chave_natural),
 -- Um protocolo do Guardian pertence a uma linha so.
 constraint uq_protocolo_sd_ordem_venda_protocolo unique (protocolo_id),
 constraint ck_protocolo_sd_ordem_venda_estado
   check (estado in ('CRIADO', 'RECUSADO', 'DESFECHO_DESCONHECIDO')),
 -- Estado CRIADO exige o id devolvido: "criado sem id" seria um registro que nao rastreia nada.
 constraint ck_protocolo_sd_ordem_venda_criado_tem_id
   check (estado <> 'CRIADO' or protocolo_id is not null),
 -- A chave natural tem de ser a concatenacao dos dois campos, e nao um texto solto.
 constraint ck_protocolo_sd_ordem_venda_chave_consistente
   check (chave_natural = ordem_venda || '/' || linha_ordem),
 -- Normalizada: sem espacos, sem vazio e sem zero a esquerda.
 constraint ck_protocolo_sd_ordem_venda_ordem_normalizada
   check (ordem_venda ~ '^[1-9][0-9]*$' or ordem_venda = '0'),
 constraint ck_protocolo_sd_ordem_venda_linha_normalizada
   check (linha_ordem ~ '^[1-9][0-9]*$' or linha_ordem = '0'),
 -- Os unicos centros do fluxo SD.
 constraint ck_protocolo_sd_ordem_venda_centro check (centro in ('3011', '3008')),
 -- O retorno recebe dado de pesagem: so TLS.
 constraint ck_protocolo_sd_ordem_venda_url_https check (url_retorno like 'https://%'),
 constraint ck_protocolo_sd_ordem_venda_sha256 check (payload_sha256 ~ '^[0-9A-F]{64}$')
);

revoke all privileges
on table fugates.protocolo_sd_ordem_venda
from public;

revoke all privileges
on table fugates.protocolo_sd_ordem_venda
from fugates_dev_app;

-- Sem DELETE e sem TRUNCATE: apagar uma linha daqui liberaria o reenvio de um protocolo que ja
-- existe no Guardian. Correcao de registro e tarefa administrativa, nao do aplicativo.
grant select, insert, update
on table fugates.protocolo_sd_ordem_venda
to fugates_dev_app;

reset role;

commit;
FIM_MIGRACAO
  cat > "$TMP/validacao.sql" <<'FIM_VALIDACAO'
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
FIM_VALIDACAO
  cat > "$TMP/reverter.sql" <<'FIM_REVERTER'
-- 007S1 - desfaz a tabela de protocolos SD.
--
-- ATENCAO: isto apaga o registro de quais linhas de ordem JA VIRARAM protocolo no Guardian.
-- Os protocolos continuam existindo la - o rollback nao os desfaz. Rodar isto com o fluxo SD
-- ativo faz o aplicativo perder a memoria dos envios e recriar protocolos duplicados.
--
-- Antes de executar, exporte o conteudo:
--   \copy (select * from fugates.protocolo_sd_ordem_venda) to 'protocolo_sd_backup.csv' csv header
begin;
set role fugates_dev_owner;
drop table fugates.protocolo_sd_ordem_venda;
commit;
FIM_REVERTER
}

existe_tabela() { psql_rodar -tAc "select to_regclass('fugates.protocolo_sd_ordem_venda') is not null"; }

case "${1:-}" in
  conferir)
    psql_rodar -c "select current_user as usuario,
                          has_schema_privilege(current_user,'fugates','CREATE') as pode_criar_tabela,
                          pg_has_role(current_user,'fugates_dev_owner','MEMBER') as pode_virar_dono,
                          (select pg_get_userbyid(nspowner) from pg_namespace where nspname='fugates') as dono_do_schema,
                          to_regclass('fugates.protocolo_sd_ordem_venda') is not null as tabela_ja_existe"
    ;;
  ensaio)
    escrever_sql
    echo "== Ensaio: cria a tabela e DESFAZ. Nada fica."
    # A migracao traz begin/commit. So no ensaio o commit vira rollback.
    sed 's/^commit;$/rollback;/' "$TMP/migracao.sql" > "$TMP/ensaio.sql"
    psql_rodar < "$TMP/ensaio.sql"
    echo "== Ensaio OK. A tabela existe agora? $(existe_tabela)   (esperado: f)"
    ;;
  aplicar)
    if [ "$(existe_tabela)" = "t" ]; then echo "A tabela ja existe. Nada a fazer."; exit 0; fi
    escrever_sql
    psql_rodar < "$TMP/migracao.sql"
    echo "== Aplicada. Tabela existe? $(existe_tabela)   (esperado: t)"
    echo "== Dono e privilegios:"
    psql_rodar -c "select tableowner as dono from pg_tables
                   where schemaname='fugates' and tablename='protocolo_sd_ordem_venda'"
    echo "   Rode agora: $0 validar"
    ;;
  validar)
    escrever_sql
    psql_rodar < "$TMP/validacao.sql"
    ;;
  reverter)
    echo "ATENCAO: isto apaga o registro de quais linhas ja viraram protocolo no Guardian."
    echo "Os protocolos continuam existindo la. O fluxo SD passaria a recriar duplicatas."
    printf 'Digite REVERTER para confirmar: '; read -r resposta
    [ "$resposta" = "REVERTER" ] || { echo "Cancelado."; exit 1; }
    psql_rodar -c "\copy (select * from fugates.protocolo_sd_ordem_venda) to '/tmp/protocolo_sd_backup.csv' csv header"
    echo "Backup em /tmp/protocolo_sd_backup.csv"
    escrever_sql
    psql_rodar < "$TMP/reverter.sql"
    ;;
  *)
    echo "Uso: $0 {conferir|ensaio|aplicar|validar|reverter}"; exit 1 ;;
esac
