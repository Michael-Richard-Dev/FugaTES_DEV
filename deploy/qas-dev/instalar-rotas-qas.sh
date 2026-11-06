#!/usr/bin/env bash
# Acrescenta ao nginx as duas rotas do QAS120: /venda-qas120 e /recebimento-qas120.
#
# POR QUE ROTAS COM SUFIXO, E NAO UM SERVER PROPRIO. As rotas do receptor sao CONSTANTES no
# codigo (DevolutivaSdEndpoint.Route e GuardianCallbackEndpoint.Route). O receptor do QAS serve os
# mesmos caminhos, na porta dele. Entao quem traduz /venda-qas120 para /api/guardian/set26/callback/
# venda e o nginx - o proxy_pass leva o caminho canonico no fim. Mexer na constante criaria duas
# verdades no codigo, uma por ambiente, e seria a origem do proximo cruzamento.
#
# O QUE E DERIVADO, E POR QUE. A lista de origens permitidas NAO e digitada aqui: ela e copiada,
# linha por linha, do bloco /venda que esta no ar hoje. O Guardian e o MESMO nos dois ambientes,
# entao a lista tem de ser a mesma - e redigitar uma lista de IPs e como se escreve um buraco.
#
# COMO ELE SE PROVA. Retrata a resposta de todas as rotas do DEV antes e depois. Se qualquer uma
# mudar, ou se o nginx -t recusar, o arquivo volta de onde veio e o nginx recarrega. O DEV esta em
# producao: a unica mudanca aceitavel e nenhuma.
#
# Uso:  sudo ./instalar-rotas-qas.sh {conferir|aplicar|reverter}
set -euo pipefail

DIR="${DIR:-/etc/nginx/sites-enabled}"
GUARDA="${GUARDA:-/var/backups/fugates/nginx}"
PORTA_QAS="${PORTA_QAS:-18087}"
NGINX="${NGINX:-nginx}"

CANONICA_VENDA=/api/guardian/set26/callback/venda
CANONICA_RECEB=/api/guardian/set26/callback/recebimento
# A rota de ENTRADA do SAP: e por ela que o SAP manda contrato para o FugaTES. Nao e do Guardian,
# mas o desenho e o mesmo - constante no codigo, sufixo traduzido pelo nginx.
CANONICA_SAP=/api/v1/integracoes/sap/contratos

# UMA LISTA SO, "rota do QAS : caminho canonico do DEV".
#
# Antes eram tres lugares repetindo o mesmo par - a declaracao, a conferencia da copia e a
# montagem. Acrescentar a rota do SAP exigiria lembrar dos tres; esquecer um deixaria a rota
# existir sem ser conferida, ou ser conferida sem existir.
PARES=(
  "/venda-qas120:$CANONICA_VENDA"
  "/recebimento-qas120:$CANONICA_RECEB"
  "/contratos-sap-qas120:$CANONICA_SAP"
)

# As rotas do DEV, que NAO podem mudar de resposta.
ROTAS_DEV=(
  /api/guardian/set26/callback/
  "$CANONICA_RECEB"
  "$CANONICA_VENDA"
  "$CANONICA_SAP"
  /api/guardian/masterdata/transportadoras/callback
)
ROTAS_QAS=()
for par in "${PARES[@]}"; do ROTAS_QAS+=("${par%%:*}"); done

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

CONCLUIDO=0
FALHOU=""
set -E
trap 'FALHOU=$LINENO' ERR
trap '[ "$CONCLUIDO" = 1 ] || echo "
INTERROMPIDO na linha ${FALHOU:-?}, sem concluir. Defeito DESTE script: me mande esta saida." >&2' EXIT

# O unico arquivo de configuracao corrente em sites-enabled.
#
# Se houver mais de um, PARA. Em 06/11/2026 o nginx estava lendo tres copias do mesmo arquivo e
# qualquer edicao era um tiro no escuro; o limpar-nginx.sh resolveu isso. Se voltou a ter mais de
# um, resolver aquilo vem antes de acrescentar rota.
alvo() {
  local n achados
  achados="$(find "$DIR" -maxdepth 1 \( -type f -o -type l \) 2>/dev/null | sort)"
  n="$(printf '%s\n' "$achados" | grep -c . || true)"
  if [ "$n" != 1 ]; then
    echo "Esperava UM arquivo em $DIR, achei $n:" >&2
    printf '%s\n' "$achados" | sed 's/^/   /' >&2
    echo "Rode o limpar-nginx.sh antes." >&2
    return 1
  fi
  printf '%s' "$achados"
}

retratar() {
  local r
  for r in "${ROTAS_DEV[@]}" "${ROTAS_QAS[@]}"; do
    printf '%-52s %s\n' "$r" "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1$r" || echo ERRO)"
  done
}

# O bloco "location = <rota> { ... }" que esta no ar, pelo fechamento de chaves.
#
# Contar chaves, e nao casar ate o primeiro "}": o bloco tem "if" e "add_header" dentro, e parar
# no primeiro fechamento entregaria meio bloco - que e pior do que nenhum, porque PARECE certo.
bloco_de() {
  awk -v rota="$1" '
    index($0, "location") && index($0, rota) && index($0, "{") && !achou {
      achou=1; prof=0
    }
    achou {
      print
      n=gsub(/\{/,"{"); m=gsub(/\}/,"}")
      prof += n - m
      if (prof <= 0) exit
    }
  ' "$2"
}

# As linhas allow/deny/satisfy/auth do bloco do DEV, que valem igual para o QAS.
origens_de() {
  bloco_de "$1" "$2" | grep -E '^[[:space:]]*(allow|deny|satisfy)[[:space:]]' || true
}

# O bloco do QAS e o bloco do DEV COPIADO, com tres trocas pontuais - e nada mais.
#
# A PRIMEIRA VERSAO MONTAVA UM BLOCO NOVO a partir das linhas allow/deny que ela extraia. O
# conferir na VM, em 06/11/2026, mostrou o que isso perdia no bloco real:
#
#   1. O "limit_except GET POST" INTERNO sumia, e as linhas dele vinham achatadas para o topo.
#      No DEV, 192.168.254.101/32 pode GET e POST mas NAO outros metodos; no meu bloco nao havia
#      restricao de metodo nenhuma. A lista de origens parecia a mesma - e era, para GET e POST -
#      entao a perda nao apareceria em nenhuma conferencia de acesso.
#
#   2. 'proxy_set_header Authorization "";' sumia. O DEV LIMPA o Authorization antes de entregar
#      ao aplicativo, de proposito. O meu repassava.
#
#   3. O DEV usa 'X-Forwarded-For $remote_addr' - o peer real, sem concatenar nada. Eu escrevi
#      '$proxy_add_x_forwarded_for', que APENSA o cabecalho que o cliente mandou. E justamente o
#      vetor de falsificacao que a forma do DEV evita.
#
# Nenhuma das tres daria erro. As tres sao afrouxamento silencioso de um controle que ja estava
# certo. Copiar o bloco inteiro e mais simples e nao tem como esquecer o que eu nao sabia que
# estava la.
montar_rota() {   # $1 rota do QAS, $2 caminho canonico, $3 arquivo
  printf '\n'
  printf '    # QAS120 - acrescentado em %s pelo instalar-rotas-qas.sh\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '    # Este bloco e o de %s COPIADO. Mudam tres coisas:\n' "$2"
  printf '    #   o location (sufixo -qas120), o proxy_pass (porta %s + caminho canonico, porque a\n' "$PORTA_QAS"
  printf '    #   rota e constante no codigo) e o arquivo de log. O controle de acesso, o\n'
  printf '    #   limit_except e o tratamento de cabecalhos sao os mesmos do DEV, de proposito.\n'
  bloco_de "$2" "$3" \
    | sed -E "1s#location[[:space:]]*=[[:space:]]*[^ {]+#location = $1#" \
    | sed -E "s#proxy_pass[[:space:]]+http://127\.0\.0\.1:[0-9]+[^;]*;#proxy_pass http://127.0.0.1:$PORTA_QAS$2;#" \
    | sed -E "s#(access_log[[:space:]]+[^ ]+)\.log#\1-qas120.log#"
}

# O bloco gerado perdeu alguma diretiva do original?
#
# Conferir o EFEITO, e nao confiar no sed: contar cada diretiva nos dois e exigir igualdade. Se
# um sed falhar calado - como tres falharam hoje - a contagem acusa.
conferir_copia() {   # $1 rota do QAS, $2 caminho canonico, $3 arquivo
  local d orig novo faltou=0
  for d in allow deny limit_except proxy_set_header access_log proxy_http_version client_max_body_size; do
    orig="$(bloco_de "$2" "$3" | grep -cE "^[[:space:]]*$d[[:space:]]" || true)"
    novo="$(montar_rota "$1" "$2" "$3" | grep -cE "^[[:space:]]*$d[[:space:]]" || true)"
    if [ "$orig" != "$novo" ]; then
      printf '   %s: o DEV tem %s, o bloco do QAS ficou com %s\n' "$d" "$orig" "$novo"; faltou=1
    fi
  done
  # O location e o proxy_pass TEM de ter mudado - se nao mudaram, o sed nao casou e o bloco do
  # QAS seria um clone do DEV, servindo a rota do DEV numa porta que nao e a dele.
  montar_rota "$1" "$2" "$3" | grep -qE "location[[:space:]]*=[[:space:]]*$1[[:space:]]*\{" || {
    printf '   o location NAO virou %s: o sed nao casou\n' "$1"; faltou=1; }
  montar_rota "$1" "$2" "$3" | grep -qE "proxy_pass[[:space:]]+http://127\.0\.0\.1:$PORTA_QAS$2;" || {
    printf '   o proxy_pass NAO aponta para %s%s\n' "$PORTA_QAS" "$2"; faltou=1; }
  return $faltou
}

case "${1:-}" in

conferir)
  A="$(alvo)" || { CONCLUIDO=1; exit 1; }
  echo "arquivo de configuracao .... $A"
  echo
  echo "o bloco /venda que esta no ar hoje, que sera COPIADO inteiro:"
  bloco_de "$CANONICA_VENDA" "$A" | sed 's/^/   /'
  echo
  # Esta lista NAO e mais o que se copia - o bloco inteiro e. Ela serve a uma pergunta so:
  # existe controle de acesso ali? Chamar isso de "origens que serao reaproveitadas", como
  # dizia ate 06/11/2026, descrevia a versao ANTERIOR do script, que achatava estas linhas num
  # bloco novo e perdia o limit_except. Rotulo que descreve o que o programa fazia ANTES e pior
  # do que rotulo nenhum: ele e lido como se fosse verdade.
  echo "ele tem controle de acesso proprio?"
  o="$(origens_de "$CANONICA_VENDA" "$A")"
  if [ -z "$o" ]; then
    echo "   NAO: nenhum allow/deny no bloco - as rotas do QAS nasceriam abertas."
    echo "   NAO aplique: confira se o controle de origem esta em outro lugar (nftables, server)."
  else
    echo "   sim, $(printf '%s\n' "$o" | grep -c .) linha(s) de allow/deny/satisfy - e elas vao"
    echo "   junto na copia, com o limit_except e o tratamento de cabecalhos."
  fi
  echo
  echo "as rotas, agora (as -qas120 devem dar 404: ainda nao existem):"
  retratar | sed 's/^/   /'
  echo
  echo "como as rotas do QAS ficariam:"
  montar_rota /venda-qas120 "$CANONICA_VENDA" "$A" | sed "s/^/   /"
  echo
  echo "o bloco do QAS perdeu alguma diretiva do original?"
  if conferir_copia /venda-qas120 "$CANONICA_VENDA" "$A"; then
    echo "   nenhuma: contagem igual em allow, deny, limit_except, proxy_set_header e access_log"
  fi
  CONCLUIDO=1
  ;;

aplicar)
  A="$(alvo)" || { CONCLUIDO=1; exit 1; }

  for r in "${ROTAS_QAS[@]}"; do
    if grep -qE "location[[:space:]]*=[[:space:]]*${r}[[:space:]]*\{" "$A"; then
      echo "A rota $r ja existe em $A. Nada foi alterado."
      echo "Para refazer: sudo $0 reverter, e depois aplicar."
      CONCLUIDO=1; exit 1
    fi
  done

  ORIGENS="$(origens_de "$CANONICA_VENDA" "$A")"
  [ -n "$ORIGENS" ] || {
    echo "O bloco $CANONICA_VENDA nao tem allow/deny: as rotas do QAS nasceriam ABERTAS."
    echo "Isto exige decisao sua, nao um padrao meu. Nada foi alterado."
    CONCLUIDO=1; exit 1; }
  echo "== $(printf '%s
' "$ORIGENS" | grep -c .) linha(s) de controle de acesso no bloco /venda"

  # A COPIA PRESERVOU TUDO? Se um sed falhar calado, o bloco do QAS sai com menos controle do que
  # o do DEV - e um allow a menos nao da erro, so deixa entrar menos; um limit_except a menos
  # deixa entrar MAIS. Conferir antes de escrever no arquivo que esta em producao.
  for par in "${PARES[@]}"; do
    if ! conferir_copia "${par%%:*}" "${par##*:}" "$A"; then
      echo "A copia do bloco ${par##*:} perdeu diretiva. Nada foi alterado."
      CONCLUIDO=1; exit 1
    fi
  done
  echo "== os ${#PARES[@]} blocos do QAS tem as mesmas diretivas de controle que os do DEV"

  # O fim do bloco /venda do DEV: e depois dele que as rotas novas entram. Ficar ao lado da rota
  # irma e proposital - quem for ler isto em janeiro acha as quatro juntas.
  # A LINHA FINAL VEM DO ARQUIVO, NAO DE ARITMETICA SOBRE CONTAGEM.
  #
  # A primeira versao fazia FIM = INICIO + (linhas do bloco) - 1, contando as linhas com
  # "grep -c ." - que conta so as NAO VAZIAS. O bloco /venda real tem linhas em branco dentro,
  # entao o FIM saia curto e as rotas do QAS eram inseridas DENTRO do location do DEV. O nginx
  # recusou com "location /venda-qas120 cannot be inside the exact location .../venda", e o desfazer
  # devolveu o arquivo - funcionou, mas e uma falha que nao deveria ter chegado na VM.
  #
  # O ENSAIO NAO TINHA COMO PEGAR: o meu "nginx -t" falso respondia sucesso para qualquer
  # conteudo. Ele provou os caminhos de desfazer e nao provou nada sobre a configuracao gerada -
  # e eu li "nginx -t aceitou" na saida do ensaio como se significasse alguma coisa.
  #
  # Contar chaves ate fechar e dizer em QUE linha fechou nao tem como divergir do arquivo.
  INICIO="$(awk -v rota="$CANONICA_VENDA" 'index($0,"location") && index($0,rota) && index($0,"{"){print NR; exit}' "$A")"
  [ -n "$INICIO" ] || { echo "Nao achei o bloco $CANONICA_VENDA em $A."; CONCLUIDO=1; exit 1; }
  FIM="$(awk -v rota="$CANONICA_VENDA" '
    index($0,"location") && index($0,rota) && index($0,"{") && !achou { achou=1; prof=0 }
    achou { n=gsub(/\{/,"{"); m=gsub(/\}/,"}"); prof += n - m; if (prof <= 0) { print NR; exit } }' "$A")"
  [ -n "$FIM" ] || { echo "O bloco $CANONICA_VENDA nao fecha em $A."; CONCLUIDO=1; exit 1; }
  # A linha do fim TEM de fechar o bloco, e tem de vir depois do inicio.
  if [ "$FIM" -le "$INICIO" ] || ! sed -n "${FIM}p" "$A" | grep -q '}'; then
    echo "Nao consegui achar o fim do bloco $CANONICA_VENDA (inicio $INICIO, fim $FIM)."
    echo "Nada foi alterado."; CONCLUIDO=1; exit 1
  fi
  echo "== o bloco /venda ocupa as linhas $INICIO a $FIM; as rotas do QAS entram depois"

  install -d -m 0750 "$GUARDA"
  COPIA="$GUARDA/$(basename "$A").antes-qas-$(date -u +%Y%m%d%H%M%SZ)"
  cp -p "$A" "$COPIA"
  echo "== copia de seguranca: $COPIA"

  echo "== retrato ANTES"
  ANTES="$(retratar)"
  printf '%s\n' "$ANTES" | sed 's/^/   /'

  NOVO="$(mktemp)"
  {
    sed -n "1,${FIM}p" "$A"
    for par in "${PARES[@]}"; do montar_rota "${par%%:*}" "${par##*:}" "$A"; done
    sed -n "$((FIM+1)),\$p" "$A"
  } > "$NOVO"
  cat "$NOVO" > "$A"
  rm -f "$NOVO"

  voltar() {
    cat "$COPIA" > "$A"
    "$NGINX" -s reload >/dev/null 2>&1 || systemctl reload nginx >/dev/null 2>&1 || true
    echo "O arquivo foi DEVOLVIDO de $COPIA e o nginx recarregado."
  }

  if ! "$NGINX" -t 2>&1 | sed 's/^/   /'; then
    echo "O nginx RECUSOU a configuracao."
    voltar; CONCLUIDO=1; exit 1
  fi
  echo "== nginx -t aceitou"

  "$NGINX" -s reload >/dev/null 2>&1 || systemctl reload nginx
  sleep 2

  echo "== retrato DEPOIS"
  DEPOIS="$(retratar)"
  printf '%s\n' "$DEPOIS" | sed 's/^/   /'

  # As rotas do DEV nao podem mudar; as do QAS TEM de mudar.
  MUDOU_DEV=0
  for r in "${ROTAS_DEV[@]}"; do
    a="$(printf '%s\n' "$ANTES"  | awk -v r="$r" '$1==r{print $2}')"
    d="$(printf '%s\n' "$DEPOIS" | awk -v r="$r" '$1==r{print $2}')"
    if [ "$a" != "$d" ]; then echo "   MUDOU no DEV: $r era $a, virou $d"; MUDOU_DEV=1; fi
  done
  if [ "$MUDOU_DEV" != 0 ]; then
    echo "Uma rota do DEV mudou de resposta. Isso basta para desfazer."
    voltar; CONCLUIDO=1; exit 1
  fi
  echo "== nenhuma rota do DEV mudou de resposta"

  AINDA_404=0
  for r in "${ROTAS_QAS[@]}"; do
    d="$(printf '%s\n' "$DEPOIS" | awk -v r="$r" '$1==r{print $2}')"
    if [ "$d" = "404" ]; then echo "   $r continua 404: o bloco nao entrou onde deveria"; AINDA_404=1
    else echo "   $r responde $d (403 daqui e o esperado: 127.0.0.1 nao e origem do Guardian)"; fi
  done
  if [ "$AINDA_404" != 0 ]; then
    echo "As rotas do QAS nao passaram a existir. Desfazendo para nao deixar meia mudanca."
    voltar; CONCLUIDO=1; exit 1
  fi

  echo
  echo "Pronto. O Guardian do QAS pode apontar para:"
  echo "   http://<este-host>/venda-qas120           (devolutiva de SD - Guardian)"
  echo "   http://<este-host>/contratos-sap-qas120   (contrato - SAP)"
  echo "   http://<este-host>/recebimento-qas120     (pesagem de recebimento - Guardian)"
  echo
  echo "  SOBRE O /recebimento-qas120: a rota existe e o nginx a entrega, mas a admissao do"
  echo "  /recebimento compara a porta local com 18086 ESCRITO NO CODIGO e exige Environment=DEV"
  echo "  (ToledoCallbackAdmission, linhas 36 e 60). Em $PORTA_QAS ela recusa com"
  echo "  TOLEDO_SOURCE_NOT_ALLOWED - nao e defeito desta rota nem do ambiente."
  echo "  O SD NAO depende disso: a rota /venda nao passa por essa porteira; o controle dela e"
  echo "  esta lista do nginx mais o listener em loopback."
  echo "  Para o recebimento do QAS andar, o par (Environment, porta) precisa ser estendido no"
  echo "  codigo - mesma forma do par (banco, papel) de hoje. Nao e trabalho de hoje."
  CONCLUIDO=1
  ;;

reverter)
  A="$(alvo)" || { CONCLUIDO=1; exit 1; }
  ULTIMA="$(ls -1t "$GUARDA"/*.antes-qas-* 2>/dev/null | head -1 || true)"
  [ -n "$ULTIMA" ] || { echo "Nao achei copia .antes-qas-* em $GUARDA."; CONCLUIDO=1; exit 1; }
  echo "Isto devolve $A ao estado de:"
  echo "   $ULTIMA"
  printf 'Para confirmar, escreva exatamente: REVERTER ROTAS\n> '
  read -r r
  [ "$r" = "REVERTER ROTAS" ] || { echo "Nada foi alterado."; CONCLUIDO=1; exit 1; }
  cat "$ULTIMA" > "$A"
  if ! "$NGINX" -t >/dev/null 2>&1; then
    echo "O nginx recusou a configuracao ANTIGA - isto nao deveria acontecer. Veja: $NGINX -t"
    CONCLUIDO=1; exit 1
  fi
  "$NGINX" -s reload >/dev/null 2>&1 || systemctl reload nginx
  sleep 2
  echo "Devolvido. As rotas agora:"
  retratar | sed 's/^/   /'
  CONCLUIDO=1
  ;;

*)
  echo "Uso: sudo $0 {conferir|aplicar|reverter}"
  CONCLUIDO=1; exit 1
  ;;
esac
