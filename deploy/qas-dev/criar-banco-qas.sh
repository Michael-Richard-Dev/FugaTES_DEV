#!/usr/bin/env bash
# Cria o banco do QAS120 no mesmo cluster do DEV110, a partir do ESTADO do fugates_dev.
#
# Escrito depois do inventariar-cluster.sh, e com base no que ele mostrou em 06/11/2026:
#   cluster 18/fugatesacmdev, porta 5433, PostgreSQL 18.6
#   fugates_dev: UTF8, LC_COLLATE C, dono fugates_dev_owner, 17 tabelas em "fugates" + 1 em "masterdata"
#   sete papeis, cada um com o seu conjunto de privilegios
#   pg_hba: "local all all" e REJECT; os servicos entram por 127.0.0.1 com scram-sha-256
#
# A ESTRUTURA E COPIADA DO BANCO QUE ESTA NO AR, com pg_dump --schema-only, e nao remontada a
# partir das migracoes de docs/Incrementos. Aquelas descrevem o HISTORICO; o que precisa existir
# no QAS e o ESTADO, e as duas coisas podem ter divergido - ha migracao de 007 ainda nao commitada.
#
# NENHUMA SENHA E CRIADA, LIDA OU IMPRESSA. Os papeis nascem com LOGIN e SEM senha, o que com
# scram-sha-256 significa que ainda nao autenticam. Quem define cada senha e voce, depois, com
# "\password", que pergunta sem ecoar e sem passar pelo historico. Ver o fim de "aplicar".
#
# O fugates_dev NAO E ALTERADO por "aplicar": ele e so LIDO. A unica rotina que mexe nele e
# "blindar-dev", que e separada de proposito e tem o seu proprio aviso.
#
# Uso:  sudo ./criar-banco-qas.sh {conferir|ensaio|aplicar|validar|blindar-dev|reverter}
set -euo pipefail

PORTA="${PORTA:-5433}"
DB_DEV="${DB_DEV:-fugates_dev}"
DB_QAS="${DB_QAS:-fugates_qas}"
DONO_QAS="${DONO_QAS:-fugates_qas_owner}"

# Os papeis de aplicacao, espelhando um a um os do DEV com os MESMOS privilegios.
# Formato:  papel_qas|papel_dev_espelhado|privilegios nas tabelas do esquema fugates
PAPEIS=(
  "fugates_qas_app|fugates_dev_app|INSERT,SELECT,UPDATE"
  "fugates_qas_read_app|fugates_dev_read_app|SELECT"
  "fugates_qas_acm_app|fugates_acm_hml_app|INSERT,SELECT,UPDATE"
  "fugates_qas_mm_app|fugates_mm_app|INSERT,SELECT"
  "fugates_qas_sap_inbound_app|fugates_sap_inbound_app|INSERT,SELECT"
  "fugates_qas_sd_app|fugates_sd_app|INSERT,SELECT,UPDATE"
)

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
chmod 700 "$TMP"

pg() { sudo -u postgres psql -p "$PORTA" -d "$1" -w -X -v ON_ERROR_STOP=1 "${@:2}"; }
pg_q() { sudo -u postgres psql -p "$PORTA" -d "$1" -w -X -At -F'|' -v ON_ERROR_STOP=1 -c "$2"; }

existe_banco() { pg_q postgres "select 1 from pg_database where datname='$1'" | grep -q 1; }
existe_papel() { pg_q postgres "select 1 from pg_roles where rolname='$1'" | grep -q 1; }

# ------------------------------------------------------------------------------------ conferir

mostrar_estado() {
  echo "cluster .......... porta $PORTA"
  echo "banco de origem .. $DB_DEV  ($(pg_q postgres "select pg_size_pretty(pg_database_size('$DB_DEV'))"))"
  echo "  tabelas ........ $(pg_q "$DB_DEV" "select count(*) from pg_tables where schemaname in ('fugates','masterdata')")"
  echo "  encoding ....... $(pg_q postgres "select pg_encoding_to_char(encoding)||' / '||datcollate from pg_database where datname='$DB_DEV'")"
  echo
  if existe_banco "$DB_QAS"; then
    echo "banco de destino . $DB_QAS JA EXISTE  ($(pg_q "$DB_QAS" "select count(*) from pg_tables where schemaname in ('fugates','masterdata')") tabela(s))"
  else
    echo "banco de destino . $DB_QAS ainda nao existe"
  fi
  echo
  echo "papeis do QAS:"
  existe_papel "$DONO_QAS" && echo "  $DONO_QAS ....... existe" || echo "  $DONO_QAS ....... ausente"
  for linha in "${PAPEIS[@]}"; do
    papel="${linha%%|*}"
    if existe_papel "$papel"; then
      senha="$(pg_q postgres "select case when rolpassword is null then 'SEM SENHA (nao autentica ainda)' else 'com senha definida' end from pg_authid where rolname='$papel'")"
      echo "  $papel ... existe, $senha"
    else
      echo "  $papel ... ausente"
    fi
  done
  echo
  echo "quem alcanca cada banco pelo PUBLIC:"
  for b in "$DB_DEV" "$DB_QAS"; do
    existe_banco "$b" || continue
    if pg_q postgres "select has_database_privilege('public','$b','CONNECT')" | grep -qi '^t'; then
      echo "  $b: PUBLIC TEM connect - qualquer papel do cluster abre este banco"
    else
      echo "  $b: PUBLIC nao tem connect - so quem recebeu explicitamente"
    fi
  done
}

# ---------------------------------------------------------------------------- o retrato da origem

extrair_estrutura() {
  # --schema-only: nenhuma linha de dado sai do DEV.
  # --no-owner e --no-privileges: o dump nao carrega os papeis do DEV para dentro do QAS; dono e
  #   privilegios sao postos aqui, explicitamente, com os papeis do QAS.
  sudo -u postgres pg_dump -p "$PORTA" -d "$DB_DEV" --schema-only --no-owner --no-privileges \
    > "$TMP/estrutura.sql"
  # SET ROLE antes de tudo: os objetos nascem pertencendo ao dono do QAS, e nao ao postgres.
  printf 'SET ROLE %s;\n' "$DONO_QAS" | cat - "$TMP/estrutura.sql" > "$TMP/estrutura-qas.sql"
}

# ------------------------------------------------------------------------------------- aplicar

case "${1:-}" in

conferir)
  mostrar_estado
  ;;

ensaio)
  echo "Nada sera alterado. Isto so prova que a estrutura do $DB_DEV pode ser extraida e lida."
  echo
  extrair_estrutura
  echo "estrutura extraida .. $(wc -l < "$TMP/estrutura.sql") linha(s)"
  echo "CREATE TABLE ........ $(grep -c '^CREATE TABLE' "$TMP/estrutura.sql" || true)"
  echo "CREATE INDEX ........ $(grep -c '^CREATE INDEX\|^CREATE UNIQUE INDEX' "$TMP/estrutura.sql" || true)"
  echo "CREATE SCHEMA ....... $(grep -c '^CREATE SCHEMA' "$TMP/estrutura.sql" || true)"
  echo "linhas com INSERT ... $(grep -c '^INSERT' "$TMP/estrutura.sql" || true)   (tem de ser 0: schema-only)"
  echo
  echo "Os papeis que SERIAM criados, todos sem senha:"
  echo "  $DONO_QAS  (dono do banco e dos esquemas)"
  for linha in "${PAPEIS[@]}"; do
    IFS='|' read -r papel espelho privs <<< "$linha"
    printf '  %-30s espelha %-26s %s\n' "$papel" "$espelho" "$privs"
  done
  ;;

aplicar)
  if existe_banco "$DB_QAS"; then
    n="$(pg_q "$DB_QAS" "select count(*) from pg_tables where schemaname in ('fugates','masterdata')")"
    echo "$DB_QAS ja existe, com $n tabela(s)."
    echo
    if [ "$n" = "0" ]; then
      echo "Vazio: provavelmente um aplicar que parou no meio. Como nao ha dado a perder,"
      echo "o caminho limpo e recomecar - o reverter derruba o banco e os papeis do QAS,"
      echo "e nao toca no $DB_DEV:"
      echo
      echo "  sudo $0 reverter"
      echo "  sudo $0 aplicar"
    else
      echo "TEM TABELAS. Nao vou por nada por cima: confira o que ha ali antes de decidir."
    fi
    exit 1
  fi

  echo "== extraindo a estrutura de $DB_DEV (somente leitura)"
  extrair_estrutura

  echo "== criando os papeis, SEM senha"
  existe_papel "$DONO_QAS" || pg postgres -c "CREATE ROLE $DONO_QAS LOGIN"
  for linha in "${PAPEIS[@]}"; do
    papel="${linha%%|*}"
    existe_papel "$papel" || pg postgres -c "CREATE ROLE $papel LOGIN"
  done

  # O encoding e o collate sao os do DEV, lidos do catalogo - nao escritos aqui.
  ENC="$(pg_q postgres "select pg_encoding_to_char(encoding) from pg_database where datname='$DB_DEV'")"
  COL="$(pg_q postgres "select datcollate from pg_database where datname='$DB_DEV'")"
  CTY="$(pg_q postgres "select datctype from pg_database where datname='$DB_DEV'")"
  echo "== criando $DB_QAS  (encoding $ENC, collate $COL)"
  pg postgres -c "CREATE DATABASE $DB_QAS OWNER $DONO_QAS TEMPLATE template0
                  ENCODING '$ENC' LC_COLLATE '$COL' LC_CTYPE '$CTY'"

  # ISOLAMENTO. Papel e do CLUSTER, nao do banco: sem isto, fugates_sd_app - que e do DEV -
  # abriria o banco do QAS, e uma cadeia de conexao com o nome do banco trocado cruzaria os
  # ambientes em silencio. O mesmo motivo de o par origem+sap-client ser conferido junto.
  echo "== revogando CONNECT do PUBLIC em $DB_QAS"
  pg postgres -c "REVOKE CONNECT ON DATABASE $DB_QAS FROM PUBLIC"
  pg postgres -c "GRANT CONNECT ON DATABASE $DB_QAS TO $DONO_QAS"
  for linha in "${PAPEIS[@]}"; do
    papel="${linha%%|*}"
    pg postgres -c "GRANT CONNECT ON DATABASE $DB_QAS TO $papel"
  done

  # POR STDIN, e nao por -f: o diretorio temporario e do root, em modo 700, e o psql roda como
  # postgres - ele nao leria o arquivo ("Permission denied", em 06/11/2026). Com o redirecionamento
  # o arquivo e aberto pelo ROOT, antes de o sudo baixar privilegio, e o postgres recebe o
  # descritor ja aberto. Afrouxar o diretorio resolveria tambem, e deixaria a estrutura do banco
  # legivel por qualquer um na maquina - o que nao e preciso.
  echo "== carregando a estrutura"
  pg "$DB_QAS" -f - < "$TMP/estrutura-qas.sql" >/dev/null

  echo "== concedendo privilegios, espelhando o DEV"
  for linha in "${PAPEIS[@]}"; do
    IFS='|' read -r papel espelho privs <<< "$linha"
    pg "$DB_QAS" -c "GRANT USAGE ON SCHEMA fugates, masterdata TO $papel"
    pg "$DB_QAS" -c "GRANT $privs ON ALL TABLES IN SCHEMA fugates, masterdata TO $papel"
    # Sequencia acompanha quem insere: sem USAGE, um INSERT com coluna serial falha.
    case "$privs" in *INSERT*) pg "$DB_QAS" -c "GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA fugates, masterdata TO $papel";; esac
  done

  echo
  echo "=============================================================="
  echo " Banco criado. FALTA A SENHA DE CADA PAPEL - e ela e sua."
  echo "=============================================================="
  echo
  echo "Os papeis nasceram com LOGIN e SEM senha: com scram-sha-256 eles ainda NAO autenticam."
  echo "Defina uma a uma, pelo proprio psql, que pergunta sem ecoar e nao deixa no historico:"
  echo
  echo "  sudo -u postgres psql -p $PORTA"
  for linha in "${PAPEIS[@]}"; do echo "    \\password ${linha%%|*}"; done
  echo "    \\q"
  echo
  echo "Depois, confira com:  sudo $0 validar"
  ;;

validar)
  existe_banco "$DB_QAS" || { echo "$DB_QAS nao existe."; exit 1; }
  falhas=0

  echo "== as tabelas sao as mesmas do $DB_DEV?"
  pg_q "$DB_DEV" "select schemaname||'.'||tablename from pg_tables where schemaname in ('fugates','masterdata') order by 1" > "$TMP/dev.txt"
  pg_q "$DB_QAS" "select schemaname||'.'||tablename from pg_tables where schemaname in ('fugates','masterdata') order by 1" > "$TMP/qas.txt"
  if diff -q "$TMP/dev.txt" "$TMP/qas.txt" >/dev/null; then
    echo "   SIM: $(wc -l < "$TMP/qas.txt") tabela(s), identicas."
  else
    echo "   NAO. Diferencas:"; diff "$TMP/dev.txt" "$TMP/qas.txt" | sed 's/^/     /'; falhas=$((falhas+1))
  fi

  echo "== o QAS esta vazio de dados?"
  vazio=1
  while read -r t; do
    [ -n "$t" ] || continue
    n="$(pg_q "$DB_QAS" "select count(*) from $t")"
    [ "$n" = "0" ] || { echo "   $t tem $n linha(s)"; vazio=0; }
  done < "$TMP/qas.txt"
  [ "$vazio" = 1 ] && echo "   SIM: nenhuma linha. A estrutura veio sem dado, como deve." || falhas=$((falhas+1))

  echo "== o PUBLIC foi revogado?"
  if pg_q postgres "select has_database_privilege('public','$DB_QAS','CONNECT')" | grep -qi '^f'; then
    echo "   SIM: so quem recebeu CONNECT explicitamente abre $DB_QAS."
  else
    echo "   NAO: PUBLIC ainda alcanca $DB_QAS."; falhas=$((falhas+1))
  fi

  echo "== os papeis do DEV alcancam o QAS?"
  for p in fugates_dev_app fugates_dev_read_app fugates_acm_hml_app fugates_mm_app fugates_sap_inbound_app fugates_sd_app; do
    existe_papel "$p" || continue
    if pg_q postgres "select has_database_privilege('$p','$DB_QAS','CONNECT')" | grep -qi '^t'; then
      echo "   $p ALCANCA $DB_QAS - nao deveria"; falhas=$((falhas+1))
    fi
  done
  [ "$falhas" = 0 ] && echo "   Nenhum alcanca. Os ambientes estao separados por credencial."

  # DE pg_authid, E NAO DE pg_roles. A view pg_roles devolve a constante '********' no campo
  # rolpassword para TODO papel, entao "rolpassword is not null" nela e SEMPRE verdadeiro - a
  # conferencia passava com os seis papeis recem-criados, nenhum deles com senha. Era um
  # verificador que nunca reprova, que e o pior tipo: ele nao erra de vez em quando, ele mente
  # sempre, e com cara de prova. Em 06/11/2026 chegou a dizer "VALIDADO: 0 pendencia(s)" sobre um
  # banco cujos papeis nao autenticavam.
  #
  # pg_authid guarda o valor de verdade e so superusuario le - aqui somos postgres. O valor nunca
  # e selecionado: so se ele EXISTE.
  echo "== cada papel do QAS tem senha?"
  for linha in "${PAPEIS[@]}"; do
    papel="${linha%%|*}"
    if pg_q postgres "select rolpassword is not null from pg_authid where rolname='$papel'" | grep -qi '^t'; then
      echo "   $papel ... sim"
    else
      echo "   $papel ... AINDA SEM SENHA (nao autentica)"; falhas=$((falhas+1))
    fi
  done

  # O DONO fica DE PROPOSITO sem senha, e isto e dito em voz alta para a omissao nao parecer
  # descuido. O DDL do QAS vai pelo mesmo caminho do DEV - "sudo -u postgres" com "set role" -,
  # exatamente como os migrar-*.sh fazem hoje, porque tambem nao ha credencial do
  # fugates_dev_owner nesta maquina. Sem senha ele nao autentica de lugar nenhum, que e o estado
  # mais estreito possivel para um papel que so precisa ser DONO, e nunca cliente.
  if pg_q postgres "select rolpassword is not null from pg_authid where rolname='$DONO_QAS'" | grep -qi '^t'; then
    echo "   $DONO_QAS ... tem senha. Nao e preciso: o DDL vai por sudo -u postgres com set role."
  else
    echo "   $DONO_QAS ... sem senha, DE PROPOSITO (so dono, nunca cliente)."
  fi

  echo
  [ "$falhas" = 0 ] && echo "VALIDADO: $falhas pendencia(s)." || { echo "$falhas pendencia(s) acima."; exit 1; }
  ;;

blindar-dev)
  # SEPARADA DE PROPOSITO: isto ALTERA o banco do DEV, que esta em uso.
  #
  # Em 06/11/2026 esta rotina nasceu de uma SUPOSICAO minha - a de que o PUBLIC alcancava o
  # fugates_dev. O conferir mostrou que nao alcanca: o banco do DEV ja estava fechado. A rotina
  # fica porque o dia em que alguem conceder CONNECT ao PUBLIC por engano ela resolve, mas agora
  # ela confere ANTES, em vez de agir sobre a suposicao.
  if pg_q postgres "select has_database_privilege('public','$DB_DEV','CONNECT')" | grep -qi '^f'; then
    echo "Nada a fazer: o PUBLIC ja NAO tem CONNECT em $DB_DEV."
    echo "Os dois bancos ja estao separados por credencial, nos dois sentidos."
    exit 0
  fi

  echo "Esta rotina revoga CONNECT do PUBLIC em $DB_DEV e concede explicitamente aos papeis do DEV."
  echo
  echo "POR QUE: hoje o PUBLIC tem CONNECT em $DB_DEV, entao QUALQUER papel do cluster - inclusive"
  echo "os novos do QAS - consegue abrir o banco do DEV. O isolamento posto no $DB_QAS so vale"
  echo "numa direcao enquanto isto nao for feito."
  echo
  echo "RISCO: se algum papel ou ferramenta depender do CONNECT via PUBLIC e nao estiver na lista"
  echo "abaixo, ele perde acesso na hora. Os que receberao CONNECT explicito:"
  for p in fugates_dev_owner fugates_dev_app fugates_dev_read_app fugates_acm_hml_app fugates_mm_app fugates_sap_inbound_app fugates_sd_app; do
    existe_papel "$p" && echo "   $p"
  done
  echo
  printf 'Para confirmar, escreva exatamente: BLINDAR %s\n> ' "$DB_DEV"
  read -r resposta
  [ "$resposta" = "BLINDAR $DB_DEV" ] || { echo "Nada foi alterado."; exit 1; }

  for p in fugates_dev_owner fugates_dev_app fugates_dev_read_app fugates_acm_hml_app fugates_mm_app fugates_sap_inbound_app fugates_sd_app; do
    existe_papel "$p" && pg postgres -c "GRANT CONNECT ON DATABASE $DB_DEV TO $p"
  done
  pg postgres -c "REVOKE CONNECT ON DATABASE $DB_DEV FROM PUBLIC"
  echo "Feito. Confira os servicos do DEV antes de sair: eles reconectam a cada volta."
  ;;

reverter)
  echo "Isto DERRUBA $DB_QAS e os papeis do QAS. O $DB_DEV nao e tocado."
  printf 'Para confirmar, escreva exatamente: REVERTER %s\n> ' "$DB_QAS"
  read -r resposta
  [ "$resposta" = "REVERTER $DB_QAS" ] || { echo "Nada foi alterado."; exit 1; }
  existe_banco "$DB_QAS" && pg postgres -c "DROP DATABASE $DB_QAS"
  for linha in "${PAPEIS[@]}"; do
    papel="${linha%%|*}"
    existe_papel "$papel" && pg postgres -c "DROP ROLE $papel"
  done
  existe_papel "$DONO_QAS" && pg postgres -c "DROP ROLE $DONO_QAS"
  echo "Revertido."
  ;;

*)
  echo "Uso: sudo $0 {conferir|ensaio|aplicar|validar|blindar-dev|reverter}"
  exit 1
  ;;
esac
