#!/usr/bin/env bash
# Acrescenta ao nginx as duas rotas do QAS120: /venda-qas e /recebimento-qas.
#
# POR QUE ROTAS COM SUFIXO, E NAO UM SERVER PROPRIO. As rotas do receptor sao CONSTANTES no
# codigo (DevolutivaSdEndpoint.Route e GuardianCallbackEndpoint.Route). O receptor do QAS serve os
# mesmos caminhos, na porta dele. Entao quem traduz /venda-qas para /api/guardian/set26/callback/
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

# As rotas do DEV, que NAO podem mudar de resposta.
ROTAS_DEV=(
  /api/guardian/set26/callback/
  "$CANONICA_RECEB"
  "$CANONICA_VENDA"
  /api/guardian/masterdata/transportadoras/callback
)
ROTAS_QAS=(/venda-qas /recebimento-qas)

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

montar_rota() {   # $1 rota do QAS, $2 caminho canonico, $3 origens
  printf '\n'
  printf '    # QAS120 - acrescentado em %s pelo instalar-rotas-qas.sh\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '    # As origens abaixo foram COPIADAS do bloco %s que estava no ar.\n' "$2"
  printf '    # O proxy_pass leva o caminho CANONICO: a rota e constante no codigo, e quem\n'
  printf '    # traduz o sufixo -qas e este bloco.\n'
  printf '    location = %s {\n' "$1"
  printf '%s\n' "$3" | sed 's/^[[:space:]]*/        /'
  printf '        proxy_pass http://127.0.0.1:%s%s;\n' "$PORTA_QAS" "$2"
  printf '        proxy_http_version 1.1;\n'
  printf '        proxy_set_header Host $host;\n'
  printf '        proxy_set_header X-Real-IP $remote_addr;\n'
  printf '        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;\n'
  printf '        client_max_body_size 2m;\n'
  printf '    }\n'
}

case "${1:-}" in

conferir)
  A="$(alvo)" || { CONCLUIDO=1; exit 1; }
  echo "arquivo de configuracao .... $A"
  echo
  echo "o bloco /venda que esta no ar hoje, de onde as origens serao copiadas:"
  bloco_de "$CANONICA_VENDA" "$A" | sed 's/^/   /'
  echo
  echo "as origens que serao reaproveitadas:"
  o="$(origens_de "$CANONICA_VENDA" "$A")"
  if [ -z "$o" ]; then
    echo "   NENHUMA. O bloco /venda nao tem allow/deny - as rotas do QAS nasceriam abertas."
    echo "   NAO aplique: confira se o controle de origem esta em outro lugar (nftables, server)."
  else
    printf '%s\n' "$o" | sed 's/^[[:space:]]*/   /'
  fi
  echo
  echo "as rotas, agora (as -qas devem dar 404: ainda nao existem):"
  retratar | sed 's/^/   /'
  echo
  echo "como as rotas do QAS ficariam:"
  montar_rota /venda-qas "$CANONICA_VENDA" "$o" | sed 's/^/   /'
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
  echo "== $(printf '%s\n' "$ORIGENS" | grep -c .) linha(s) de origem copiadas do bloco /venda"

  # O fim do bloco /venda do DEV: e depois dele que as rotas novas entram. Ficar ao lado da rota
  # irma e proposital - quem for ler isto em janeiro acha as quatro juntas.
  INICIO="$(awk -v rota="$CANONICA_VENDA" 'index($0,"location") && index($0,rota) && index($0,"{"){print NR; exit}' "$A")"
  [ -n "$INICIO" ] || { echo "Nao achei o bloco $CANONICA_VENDA em $A."; CONCLUIDO=1; exit 1; }
  QUANTAS="$(bloco_de "$CANONICA_VENDA" "$A" | grep -c .)"
  FIM=$((INICIO + QUANTAS - 1))
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
    montar_rota /venda-qas       "$CANONICA_VENDA" "$ORIGENS"
    montar_rota /recebimento-qas "$CANONICA_RECEB" "$ORIGENS"
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
  echo "   http://<este-host>/venda-qas          (devolutiva de SD)"
  echo "   http://<este-host>/recebimento-qas    (pesagem de recebimento)"
  echo
  echo "  SOBRE O /recebimento-qas: a rota existe e o nginx a entrega, mas a admissao do"
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
