#!/usr/bin/env bash
# Inventario do cluster PostgreSQL da VM, para planejar o banco do QAS120.
#
# NAO ALTERA NADA. Nenhum CREATE, nenhum GRANT, nenhuma escrita - so consultas de catalogo.
#
# POR QUE ISTO VEM ANTES DO SCRIPT DE CRIACAO. O banco do DEV110 nasceu ao longo de semanas, em
# migracoes espalhadas por docs/Incrementos/005, 006 e 007, algumas ainda nao commitadas. Escrever
# o "criar fugates_qas" a partir daquela lista seria recriar o HISTORICO, e o que precisa existir
# e o ESTADO - que pode ter divergido. Este inventario diz o que de fato esta no ar.
#
# Tambem ha o que eu nao posso adivinhar: como os servicos autenticam no banco, quais papeis
# existem, se o cluster e um so. Escrever o script de criacao sem isso produziria algo que falha
# na metade, num cluster que ja atende producao do DEV.
#
# NENHUMA SENHA E LIDA OU IMPRESSA. O catalogo guarda hash, e nem o hash e consultado aqui. O
# pg_hba mostra o METODO de autenticacao, que e o que precisamos saber, e nao segredo nenhum.
#
# Uso:  sudo ./inventariar-cluster.sh
set -euo pipefail

PORTA="${PORTA:-5433}"
DB_DEV="${DB_DEV:-fugates_dev}"
DB_QAS="${DB_QAS:-fugates_qas}"

[ "$(id -u)" = 0 ] || { echo "Rode com sudo (o acesso de catalogo e por sudo -u postgres)."; exit 1; }

psql_q() { sudo -u postgres psql -p "$PORTA" -d "$1" -w -X -At -F'|' -v ON_ERROR_STOP=1 -c "$2"; }

echo "=============================================================="
echo " CLUSTER"
echo "=============================================================="
echo "porta ............ $PORTA"
echo "versao ........... $(psql_q postgres 'select version()' | cut -c1-60)"
echo "data_directory ... $(psql_q postgres 'show data_directory')"
echo
echo "clusters visiveis no sistema:"
pg_lsclusters 2>/dev/null | sed 's/^/  /' || echo "  (pg_lsclusters nao disponivel)"

echo
echo "=============================================================="
echo " BANCOS"
echo "=============================================================="
psql_q postgres "select d.datname, pg_get_userbyid(d.datdba) as dono, pg_encoding_to_char(d.encoding),
       d.datcollate, pg_size_pretty(pg_database_size(d.datname))
  from pg_database d where not d.datistemplate order by d.datname" \
| awk -F'|' 'BEGIN{printf "  %-20s %-24s %-8s %-12s %s\n","BANCO","DONO","ENCODING","COLLATE","TAMANHO"}
             {printf "  %-20s %-24s %-8s %-12s %s\n",$1,$2,$3,$4,$5}'

echo
if psql_q postgres "select 1 from pg_database where datname='$DB_QAS'" | grep -q 1; then
  echo "  ATENCAO: o banco $DB_QAS JA EXISTE. O script de criacao tera de decidir o que fazer."
else
  echo "  $DB_QAS ainda nao existe."
fi

echo
echo "=============================================================="
echo " PAPEIS  (atributos, nunca senha)"
echo "=============================================================="
psql_q postgres "select r.rolname, r.rolsuper, r.rolcreatedb, r.rolcreaterole, r.rolcanlogin,
       r.rolpassword is not null as tem_senha,
       coalesce((select string_agg(g.rolname,',') from pg_auth_members m
                 join pg_roles g on g.oid=m.roleid where m.member=r.oid),'')
  from pg_authid r where r.rolname like 'fugates%' or r.rolname='postgres' order by r.rolname" \
| awk -F'|' 'BEGIN{printf "  %-26s %-6s %-7s %-7s %-6s %-6s %s\n","PAPEL","SUPER","CREATEDB","CREATEROLE","LOGIN","SENHA","MEMBRO DE"}
             {printf "  %-26s %-6s %-7s %-7s %-6s %-6s %s\n",$1,$2,$3,$4,$5,$6,$7}'
echo
echo "  (SENHA vem de pg_authid, e nao de pg_roles: a view pg_roles devolve a constante '********'
   para TODO papel, entao 'rolpassword is not null' nela e sempre verdadeiro e nao prova nada.
   Aqui le-se apenas SE existe senha; o valor nunca e consultado.)"

echo
echo "=============================================================="
echo " O BANCO DO DEV110 — esquemas, tabelas e privilegios"
echo "=============================================================="
echo "esquemas:"
psql_q "$DB_DEV" "select n.nspname, pg_get_userbyid(n.nspowner)
  from pg_namespace n where n.nspname not like 'pg_%' and n.nspname<>'information_schema'
  order by n.nspname" | sed 's/^/  /' | sed 's/|/  dono: /'

echo
echo "tabelas por esquema:"
psql_q "$DB_DEV" "select schemaname, count(*) from pg_tables
  where schemaname not like 'pg_%' and schemaname<>'information_schema'
  group by schemaname order by schemaname" | sed 's/^/  /' | sed 's/|/: /'

echo
echo "as tabelas do esquema fugates:"
psql_q "$DB_DEV" "select tablename, tableowner from pg_tables where schemaname='fugates' order by tablename" \
| awk -F'|' '{printf "  %-44s dono: %s\n",$1,$2}'

echo
echo "quem tem privilegio no esquema fugates:"
psql_q "$DB_DEV" "select grantee, string_agg(distinct privilege_type,',')
  from information_schema.table_privileges where table_schema='fugates'
  group by grantee order by grantee" | sed 's/^/  /' | sed 's/|/: /'

echo
echo "extensoes instaladas:"
psql_q "$DB_DEV" "select extname from pg_extension order by extname" | tr '\n' ' ' | sed 's/^/  /'
echo

echo
echo "=============================================================="
echo " COMO SE AUTENTICA  (metodo, nunca segredo)"
echo "=============================================================="
HBA="$(psql_q postgres 'show hba_file')"
echo "arquivo ... $HBA"
echo
grep -vE '^\s*#|^\s*$' "$HBA" 2>/dev/null | sed 's/^/  /' || echo "  (nao foi possivel ler)"

echo
echo "=============================================================="
echo " SERVICOS E SUAS CONEXOES  (chaves, nunca valores)"
echo "=============================================================="
for s in fugates-sd-protocolo fugates-toledo-callback fugates-acm-motor-dev fugates-carrier-sync; do
  estado="$(systemctl is-active "$s" 2>/dev/null || true)"
  [ -n "$estado" ] || continue
  echo "  $s ($estado)"
  # So o NOME do banco e do usuario, extraidos da forma da string de conexao. Senha nunca.
  systemctl show "$s" -p Environment --value 2>/dev/null | tr ' ' '\n' \
    | grep -iE 'database|connectionstring|npgsql' \
    | sed -E 's/(assword|pwd)=[^;"]*/\1=***/Ig' | sed 's/^/      /' || true
done

echo
echo "=============================================================="
echo " FIM — nada foi alterado."
echo "=============================================================="
