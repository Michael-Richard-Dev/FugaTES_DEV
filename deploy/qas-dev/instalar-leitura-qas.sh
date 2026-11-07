#!/usr/bin/env bash
# A API privada de leitura do QAS120: porta 18764, como SERVICO.
#
# E ela que as telas de Inicio e de Pedidos de Compra MM do desktop leem. Sem ela, as duas dizem
# "API privada indisponivel" por mais correto que esteja o resto.
#
# POR QUE COMO SERVICO. A do DEV110 (porta 18762) e um processo iniciado a mao dentro de uma
# sessao de login - conferido em 06/11/2026: PID sob session-198.scope, rodando de
# /home/michael/.fugates-unified-dev-read/. Ela morre quando aquela sessao terminar e nao volta
# num reboot, e a tela de Inicio do DEV depende dela. Este script NAO toca nela; o operador
# decidiu tratar o DEV separadamente. O QAS nasce certo.
#
# A PORTA 18764 FOI ESCOLHIDA OLHANDO. A primeira escolha foi 18763, que ja e do servico
# fugates-acm-manual-retry - so o inventario mostrou. Este script confere a porta antes de criar.
#
# A CHAVE E GERADA AQUI E NUNCA IMPRESSA. 64 bytes aleatorios, modo 0400, dono do servico. O
# desktop precisa da MESMA chave: o script diz como copia-la, e a copia e sua.
#
# Uso:  sudo ./instalar-leitura-qas.sh {conferir|aplicar|reverter}
set -euo pipefail

PORTA_QAS="${PORTA_LEITURA_QAS:-18764}"
PORTA_DEV=18762
APROVACAO='FUGATES-UNIFIED-POSTGRESQL-QAS120'
REL_CB="${REL_CB:-/opt/fugates/releases/FugaTES.Callback.Http}"
ENV_QAS="${ENV_QAS:-/etc/fugates/qas120}"
UNIDADES="${UNIDADES:-/etc/systemd/system}"
UNIDADE=fugates-leitura-qas
DEV_CB="${DEV_CB:-fugates-toledo-callback}"
QAS_CB="${QAS_CB:-fugates-callback-qas}"

SAP_QAS_URL='https://vhfufqs4ci.sap.fugacouros.com.br:44300'
SAP_QAS_CLI='120'
PASSFILE="${PASSFILE:-/etc/fugates/qas120/pg.pass}"
CHAVE="$ENV_QAS/leitura.key"
MM_DIR="${MM_DIR:-/var/lib/fugates-mm-qas}"   # sobreponivel para o ensaio, como ENV_QAS e UNIDADES

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

CONCLUIDO=0
FALHOU=""
set -E
trap 'FALHOU=$LINENO' ERR
trap '[ "$CONCLUIDO" = 1 ] || echo "
INTERROMPIDO na linha ${FALHOU:-?}, sem concluir. Defeito DESTE script: me mande esta saida." >&2' EXIT

ativo() { systemctl is-active "$1" 2>/dev/null || true; }
porta_livre() { ! ss -lnt 2>/dev/null | grep -q ":$1 "; }

# O release do receptor que o QAS ja usa: a API de leitura e o MESMO binario, com outro argumento.
release_do_qas() {
  systemctl show "$QAS_CB" -p ExecStart --value 2>/dev/null \
    | sed -n "s#.*\($REL_CB/[^/ ]*\)/FugaTES.Callback.Http.dll.*#\1#p" | head -1
}

case "${1:-}" in

conferir)
  echo "=============================================================="
  echo " O QUE EXISTE HOJE"
  echo "=============================================================="
  printf '  %-30s %s\n' "$UNIDADE" "$(ativo "$UNIDADE")"
  echo
  echo "portas de leitura:"
  for p in "$PORTA_DEV" "$PORTA_QAS"; do
    if porta_livre "$p"; then printf '   %-6s livre\n' "$p"
    else printf '   %-6s ocupada por: %s\n' "$p" "$(ss -lntp 2>/dev/null | awk -v P=":$p " '$4 ~ P {print $NF}' | head -1)"; fi
  done
  echo
  echo "o release do receptor do QAS (a API de leitura usa o MESMO binario):"
  r="$(release_do_qas)"
  if [ -n "$r" ]; then echo "   $r"; else echo "   NAO ACHEI - o $QAS_CB esta no ar?"; fi
  echo
  echo "o que falta:"
  [ -f "$PASSFILE" ] && echo "   passfile do QAS ... presente" || echo "   passfile do QAS ... AUSENTE ($PASSFILE)"
  [ -f "$CHAVE" ] && echo "   chave de leitura .. ja existe (nao sera regerada)" || echo "   chave de leitura .. sera gerada pelo aplicar"
  [ -d "$MM_DIR" ] && echo "   diretorio MM ...... presente" || echo "   diretorio MM ...... sera criado ($MM_DIR, vazio ate existir motor MM do QAS)"
  CONCLUIDO=1
  ;;

aplicar)
  REL="$(release_do_qas)"
  [ -n "$REL" ] || { echo "Nao achei o release do $QAS_CB. Ele esta no ar?"; CONCLUIDO=1; exit 1; }
  [ -f "$REL/FugaTES.Callback.Http.dll" ] || { echo "Binario ausente: $REL/FugaTES.Callback.Http.dll"; CONCLUIDO=1; exit 1; }
  echo "== binario: $REL/FugaTES.Callback.Http.dll"

  # A PORTA, CONFERIDA ANTES. 18763 parecia livre e e do fugates-acm-manual-retry; sem esta
  # conferencia a unidade subiria e morreria em "address already in use", que ninguem le como
  # "escolhi a porta de outro servico".
  if ! porta_livre "$PORTA_QAS"; then
    echo "A porta $PORTA_QAS ja esta ocupada:"
    ss -lntp 2>/dev/null | awk -v P=":$PORTA_QAS " '$4 ~ P' | sed 's/^/   /'
    echo "Escolha outra com PORTA_LEITURA_QAS=... e me avise, porque a lista no CODIGO"
    echo "(LeituraPrivadaIngresso e LocalReadTunnel) tambem precisa mudar. Nada foi alterado."
    CONCLUIDO=1; exit 1
  fi
  echo "== porta $PORTA_QAS livre"

  [ -f "$PASSFILE" ] || { echo "Falta o passfile do QAS: $PASSFILE"; CONCLUIDO=1; exit 1; }
  U="$(systemctl show "$QAS_CB" -p User --value 2>/dev/null)"; [ -n "$U" ] || U=fugates-http

  # O PAPEL DE LEITURA precisa estar no passfile. A API usa a identidade GET-only quando
  # declarada; sem a linha dela no passfile, a conexao falha com "autenticacao" e manda procurar
  # senha errada em vez de linha faltando.
  if ! grep -qE "^127\.0\.0\.1:5433:fugates_qas:fugates_qas_read_app:" "$PASSFILE"; then
    echo "O $PASSFILE nao tem linha para fugates_qas_read_app."
    echo "Acrescente, com a senha que voce definiu com \\password:"
    echo "   127.0.0.1:5433:fugates_qas:fugates_qas_read_app:<a senha>"
    echo "Nada foi alterado."
    CONCLUIDO=1; exit 1
  fi
  echo "== passfile tem a linha do papel de leitura do QAS"

  install -d -m 0750 "$ENV_QAS"
  install -d -m 0755 "$MM_DIR"
  chown "$U":"$U" "$MM_DIR" 2>/dev/null || true

  # A CHAVE. 64 bytes exatos, sem quebra de linha: a API confere o TAMANHO do arquivo, e um
  # "\n" no fim faria 65. Gerada aqui, nunca impressa, e preservada se ja existir - regerar
  # quebraria o desktop que ja a tenha.
  if [ -f "$CHAVE" ]; then
    echo "== $CHAVE ja existe, preservada"
  else
    # \r TAMBEM: no ensaio em Git Bash a chave saiu com 65 bytes, e o byte a mais era um CR que
    # o "tr -d '\n'" nao tirava. No Linux o openssl nao poe CR, entao isto nunca morderia aqui -
    # mas um byte invisivel a mais faz a API recusar com "chave rejeitada", e o conferidor de
    # tamanho logo abaixo e que salvou. Tirar os dois custa um caractere.
    openssl rand -hex 32 | tr -d '\r\n' > "$CHAVE"
    chmod 0400 "$CHAVE"; chown "$U":"$U" "$CHAVE"
    echo "== $CHAVE gerada ($(stat -c '%s' "$CHAVE") bytes, modo 0400, dono $U)"
  fi
  [ "$(stat -c '%s' "$CHAVE")" = 64 ] || {
    echo "A chave nao tem 64 bytes ($(stat -c '%s' "$CHAVE")). A API recusaria."
    CONCLUIDO=1; exit 1; }

  cat > "$UNIDADES/$UNIDADE.service" <<EOF
[Unit]
Description=FugaTES - API privada de leitura, ambiente QAS120 (porta $PORTA_QAS)
After=network-online.target postgresql.service
Wants=network-online.target

[Service]
Type=simple
User=$U
Group=$U
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=$MM_DIR
UMask=0077
StandardOutput=journal
StandardError=journal
Environment=Sap__BaseUrl=$SAP_QAS_URL
Environment=Sap__SapClient=$SAP_QAS_CLI
Environment=FUGATES_DEV_READ_APPROVAL=$APROVACAO
Environment=FUGATES_DEV_READ_KEY_FILE=$CHAVE
Environment=R165AL_APPROVED_PASSFILE=$PASSFILE
Environment=FUGATES_DEV_READ_ROLE=fugates_qas_read_app
Environment=FUGATES_DEV_MM_SNAPSHOT_DIRECTORY=$MM_DIR
WorkingDirectory=$REL
ExecStart=/usr/bin/dotnet $REL/FugaTES.Callback.Http.dll --dev-unified-read-only
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF
  chmod 0644 "$UNIDADES/$UNIDADE.service"
  echo "== unidade escrita, para voce conferir:"
  sed 's/^/      /' "$UNIDADES/$UNIDADE.service"

  systemctl daemon-reload
  systemctl enable "$UNIDADE" >/dev/null 2>&1 || true
  systemctl restart "$UNIDADE" || echo "   o systemd recusou subir $UNIDADE"
  sleep 4

  echo
  echo "== estado"
  printf '  %-30s %s\n' "$UNIDADE" "$(ativo "$UNIDADE")"
  echo "== a porta $PORTA_QAS esta servida?"
  if porta_livre "$PORTA_QAS"; then
    echo "   NAO. Veja o porque:  sudo journalctl -u $UNIDADE -n 40 --no-pager"
    CONCLUIDO=1; exit 1
  fi
  ss -lntp 2>/dev/null | awk -v P=":$PORTA_QAS " '$4 ~ P' | sed 's/^/   /'
  echo
  echo "== a leitura do DEV continua intocada?"
  if porta_livre "$PORTA_DEV"; then echo "   a $PORTA_DEV nao esta servida (ela ja estava assim?)"
  else echo "   $PORTA_DEV continua servida"; fi

  echo
  echo "=============================================================="
  echo " FALTA VOCE - a chave tem de existir dos DOIS lados"
  echo "=============================================================="
  echo "  No Windows, copie o conteudo de $CHAVE para um arquivo proprio e aponte o"
  echo "  FugaTES.unified.json do QAS para ele. A chave nao e impressa aqui: copie com"
  echo ""
  echo "     sudo cat $CHAVE"
  echo ""
  echo "  e cole num arquivo .key em C:\\Users\\TI\\AppData\\Local\\FugaTES\\QAS120\\"
  echo "  SEM quebra de linha no fim - a API confere o tamanho, e um \\n faria 65 bytes."
  echo ""
  echo "  Depois, no FugaTES.unified.json de C:\\FugaTES_QAS120:"
  echo "     \"ReadKeyFile\": o arquivo que voce acabou de criar"
  echo "     \"ApprovalReference\": \"$APROVACAO\""
  echo "     \"Tunnel\": { \"RemoteReadPort\": $PORTA_QAS }"
  CONCLUIDO=1
  ;;

reverter)
  printf 'Isto para e remove a API de leitura do QAS120. Escreva: REVERTER LEITURA\n> '
  read -r r
  [ "$r" = "REVERTER LEITURA" ] || { echo "Nada foi alterado."; CONCLUIDO=1; exit 1; }
  systemctl disable --now "$UNIDADE" >/dev/null 2>&1 || true
  rm -f "$UNIDADES/$UNIDADE.service"
  systemctl daemon-reload
  echo "Unidade removida. A chave em $CHAVE ficou - apague a mao se quiser."
  CONCLUIDO=1
  ;;

*)
  echo "Uso: sudo $0 {conferir|aplicar|reverter}"
  CONCLUIDO=1; exit 1
  ;;
esac
