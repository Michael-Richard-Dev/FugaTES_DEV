#!/usr/bin/env bash
# Tira de /etc/nginx/sites-enabled as copias .bak que o nginx esta carregando como configuracao.
#
# O QUE ESTA ERRADO HOJE. O inventario de 06/11/2026 mostrou que o nginx le TRES arquivos:
#
#   /etc/nginx/sites-enabled/fugates-hml-http.conf
#   /etc/nginx/sites-enabled/fugates-hml-http.conf.bak-sd-20261101160121Z
#   /etc/nginx/sites-enabled/fugates-hml-http.conf.bak-sd-20261101160546Z
#
# Os dois ultimos foram salvos como copia de seguranca DENTRO do diretorio que o nginx inclui por
# glob, entao viraram configuracao de verdade. Sao tres "server" ouvindo no mesmo endereco, sem
# server_name que os distinga: vence o PRIMEIRO, e os outros dois so ocupam memoria - ate alguem
# editar o arquivo errado e nao entender por que nada mudou. Isso ja aconteceu uma vez, e esta
# anotado no instalar-devolutiva-sd.sh.
#
# Enquanto estiver assim, QUALQUER alteracao naquele arquivo e um tiro no escuro - inclusive para
# o DEV, que esta em producao. Por isso a limpeza vem antes das rotas do QAS120.
#
# COMO ELE SE PROVA. Captura a resposta de cada rota do Guardian ANTES, faz a mudanca, captura
# DEPOIS, e so mantem se todas forem iguais. Qualquer diferenca - ou um nginx -t recusado -
# devolve os arquivos para onde estavam e recarrega. Nada e apagado: os arquivos sao MOVIDOS.
#
# Uso:  sudo ./limpar-nginx.sh {conferir|aplicar|reverter}
set -euo pipefail

DIR=/etc/nginx/sites-enabled
GUARDA=/var/backups/fugates/nginx
ROTAS=(
  /api/guardian/set26/callback/
  /api/guardian/set26/callback/recebimento
  /api/guardian/set26/callback/venda
  /api/guardian/masterdata/transportadoras/callback
)

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

# Os arquivos de sites-enabled que NAO sao a configuracao corrente - .bak, .backup, ~, .old.
intrusos() {
  find "$DIR" -maxdepth 1 \( -name '*.bak*' -o -name '*.backup*' -o -name '*~' -o -name '*.old' \) \
    2>/dev/null | sort
}

# A resposta de cada rota, pela porta local. O 403 e o esperado daqui: as rotas so admitem as
# origens do Guardian, e 127.0.0.1 nao e uma delas. O que importa nao e o numero, e ele NAO MUDAR.
retratar() {
  for r in "${ROTAS[@]}"; do
    printf '%s %s\n' "$r" "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1$r" || echo ERRO)"
  done
}

copias_por_rota() {
  for r in "${ROTAS[@]}"; do
    printf '%s %s\n' "$r" "$(nginx -T 2>/dev/null | grep -cE "location[ \t]*=[ \t]*${r//\//\\/}[ \t]*\{" || true)"
  done
}

case "${1:-}" in

conferir)
  echo "arquivos que o nginx REALMENTE le em $DIR:"
  nginx -T 2>/dev/null | grep -E "^# configuration file $DIR" | sed 's/^# configuration file /  /' | tr -d ':'
  echo
  echo "os que sairiam daqui:"
  if [ -z "$(intrusos)" ]; then
    echo "  (nenhum - sites-enabled ja esta limpo)"
  else
    intrusos | while read -r f; do
      printf '  %-62s %s\n' "$f" "$([ -L "$f" ] && echo '(link simbolico)' || echo '(arquivo)')"
    done
  fi
  echo
  echo "quantas copias de cada bloco o nginx tem hoje:"
  copias_por_rota | sed 's/^/  /'
  echo
  echo "  (1 e o certo. Mais de 1 significa server duplicado, e vence o primeiro.)"
  echo
  echo "o que serve hoje difere das copias?"
  VIVO="$DIR/fugates-hml-http.conf"
  intrusos | while read -r f; do
    if diff -q "$VIVO" "$f" >/dev/null 2>&1; then
      echo "  $(basename "$f") ... identico ao que serve"
    else
      echo "  $(basename "$f") ... DIFERENTE do que serve:"
      diff "$VIVO" "$f" | head -20 | sed 's/^/      /'
      echo "      (se houver algo util so na copia, decida ANTES de remove-la)"
    fi
  done
  echo
  echo "resposta de cada rota pela porta local, agora:"
  retratar | sed 's/^/  /'
  ;;

aplicar)
  ARQUIVOS="$(intrusos)"
  [ -n "$ARQUIVOS" ] || { echo "Nada a fazer: $DIR ja esta limpo."; exit 0; }

  echo "== retrato ANTES"
  retratar > /tmp/nginx-antes.txt
  cat /tmp/nginx-antes.txt | sed 's/^/   /'

  DESTINO="$GUARDA/$(date -u +%Y%m%d_%H%M%SZ)"
  install -d -m 0750 "$DESTINO"
  echo
  echo "== movendo para $DESTINO  (nada e apagado)"
  echo "$ARQUIVOS" | while read -r f; do
    [ -n "$f" ] || continue
    mv "$f" "$DESTINO/"
    echo "   $(basename "$f")"
  done

  echo
  echo "== nginx -t"
  if ! nginx -t 2>&1 | sed 's/^/   /'; then
    echo "   RECUSADO. Devolvendo os arquivos."
    mv "$DESTINO"/* "$DIR"/ 2>/dev/null || true
    nginx -t >/dev/null 2>&1 || true
    exit 1
  fi

  echo "== recarregando"
  systemctl reload nginx
  # O reload e gracioso: os processos novos assumem em seguida, nao na hora.
  sleep 2

  echo
  echo "== retrato DEPOIS"
  retratar > /tmp/nginx-depois.txt
  cat /tmp/nginx-depois.txt | sed 's/^/   /'

  echo
  if diff -q /tmp/nginx-antes.txt /tmp/nginx-depois.txt >/dev/null; then
    echo "As respostas sao IDENTICAS as de antes."
    echo
    echo "copias de cada bloco agora:"
    copias_por_rota | sed 's/^/  /'
    echo
    echo "Para voltar:  sudo $0 reverter"
    echo "Os arquivos estao em $DESTINO"
  else
    echo "AS RESPOSTAS MUDARAM. Devolvendo os arquivos e recarregando."
    diff /tmp/nginx-antes.txt /tmp/nginx-depois.txt | sed 's/^/   /'
    mv "$DESTINO"/* "$DIR"/ 2>/dev/null || true
    nginx -t >/dev/null 2>&1 && systemctl reload nginx
    exit 1
  fi
  ;;

reverter)
  ULTIMO="$(ls -1dt "$GUARDA"/*/ 2>/dev/null | head -1 || true)"
  [ -n "$ULTIMO" ] || { echo "Nao ha nada guardado em $GUARDA."; exit 1; }
  echo "Isto devolve a $DIR os arquivos de $ULTIMO:"
  ls -1 "$ULTIMO" | sed 's/^/   /'
  printf 'Para confirmar, escreva exatamente: REVERTER NGINX\n> '
  read -r resposta
  [ "$resposta" = "REVERTER NGINX" ] || { echo "Nada foi alterado."; exit 1; }
  mv "$ULTIMO"/* "$DIR"/
  nginx -t && systemctl reload nginx
  echo "Devolvidos."
  ;;

*)
  echo "Uso: sudo $0 {conferir|aplicar|reverter}"
  exit 1
  ;;
esac
