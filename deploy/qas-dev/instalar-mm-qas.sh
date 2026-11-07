#!/usr/bin/env bash
# O motor de pedidos de compra MM do QAS120.
#
# E ele que alimenta /var/lib/fugates-mm-qas, de onde a API privada de leitura serve a tela de
# Pedidos de Compra. Sem ele a tela conecta e diz "Snapshot MM indisponivel" - que e verdade.
#
# A CONFIGURACAO VAI EM /etc/fugates/qas120/mm-runtime.json, e nao no arquivo do DEV.
#
# Ate 06/11/2026 o caminho estava fixo no Program.cs, e um segundo motor leria a configuracao do
# DEV - banco, papel e diretorio de estado - por mais que a unidade dele declarasse outra coisa.
# Pior: aquele arquivo entra na configuracao DEPOIS das variaveis de ambiente, entao a unidade do
# QAS poderia declarar fugates_qas em Environment= e gravar em fugates_dev, sem erro nenhum.
# Agora o caminho sai do par SAP do processo; este script depende dessa correcao estar publicada.
#
# AS DUAS REFERENCIAS DE AUTORIZACAO SAO SUAS, E DESTE AMBIENTE. Elas nao sao copia das do DEV:
# aquelas atestam um corte do coletor Windows que aconteceu no DEV, e repeti-las aqui seria
# afirmar uma prova que nao foi feita para o QAS. As daqui dizem o que sao - a autorizacao que o
# operador deu em 06/11/2026 para criar e ligar o MM no ambiente de teste QAS120.
#
# O CallbackUrl E DO QAS, diferente do DEV de proposito: dois ambientes devolvendo no mesmo
# endereco misturariam as devolutivas, e separar nao custa nada.
#
# Uso:  sudo CALLBACK_QAS='https://...' ./instalar-mm-qas.sh {conferir|aplicar|reverter}
set -euo pipefail

REL_MT="${REL_MT:-/opt/fugates/releases/FugaTES.HostIntegracao}"
ENV_QAS="${ENV_QAS:-/etc/fugates/qas120}"
UNIDADES="${UNIDADES:-/etc/systemd/system}"
MM_DIR="${MM_DIR:-/var/lib/fugates-mm-qas}"
RUNTIME="$ENV_QAS/mm-runtime.json"
UNIDADE=fugates-mm-qas
DEV_MM="${DEV_MM:-fugates-mm-purchase-orders}"
QAS_MT="${QAS_MT:-fugates-sd-protocolo-qas}"

SAP_QAS_URL='https://vhfufqs4ci.sap.fugacouros.com.br:44300'
SAP_QAS_CLI='120'
BANCO_QAS='fugates_qas'
PAPEL_MM='fugates_qas_mm_app'
CORTE='2026-11-01'          # igual a MmCollectionPolicy.CreationCutoff; o codigo recusa outro
INTERVALO=180               # idem

ATIVACAO="${ATIVACAO:-OPERATOR_QAS120_MM_ATIVACAO_20261106}"
CORTE_WINDOWS="${CORTE_WINDOWS:-QAS120_SEM_COLETOR_WINDOWS_20261106}"

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

CONCLUIDO=0
FALHOU=""
set -E
trap 'FALHOU=$LINENO' ERR
trap '[ "$CONCLUIDO" = 1 ] || echo "
INTERROMPIDO na linha ${FALHOU:-?}, sem concluir. Defeito DESTE script: me mande esta saida." >&2' EXIT

ativo() { systemctl is-active "$1" 2>/dev/null || true; }

# O release do QAS ja instalado: o motor MM e o MESMO binario do motor SD, com outro Mode.
release_do_qas() {
  systemctl show "$QAS_MT" -p ExecStart --value 2>/dev/null \
    | sed -n "s#.*\($REL_MT/[^/ ]*\)/FugaTES.HostIntegracao.*#\1#p" | head -1
}

case "${1:-}" in

conferir)
  echo "=============================================================="
  echo " O QUE EXISTE HOJE"
  echo "=============================================================="
  printf '  %-30s %s\n' "$DEV_MM" "$(ativo "$DEV_MM")   (o do DEV, nao sera tocado)"
  printf '  %-30s %s\n' "$UNIDADE" "$(ativo "$UNIDADE")"
  echo
  r="$(release_do_qas)"
  echo "release do QAS (o motor MM usa o mesmo binario do SD):"
  [ -n "$r" ] && echo "   $r" || echo "   NAO ACHEI - o $QAS_MT esta no ar?"
  echo
  echo "o que falta:"
  [ -f "$RUNTIME" ] && echo "   $RUNTIME ... ja existe" || echo "   $RUNTIME ... sera criado"
  [ -d "$MM_DIR" ] && echo "   $MM_DIR ... presente" || echo "   $MM_DIR ... sera criado"
  echo "   CALLBACK_QAS ...................... ${CALLBACK_QAS:+informado}${CALLBACK_QAS:-NAO INFORMADO}"
  echo
  echo "o binario instalado ja deriva o caminho do runtime pelo ambiente?"
  if [ -n "$r" ] && grep -qa 'qas120/mm-runtime.json' "$r/FugaTES.HostIntegracao.dll" 2>/dev/null; then
    echo "   sim"
  else
    echo "   NAO - este motor leria /etc/fugates/mm/runtime.json, a configuracao do DEV."
    echo "   Publique o pacote do motor com a correcao antes de aplicar."
  fi
  CONCLUIDO=1
  ;;

aplicar)
  [ -n "${CALLBACK_QAS:-}" ] || {
    echo "Informe o endereco de devolutiva do QAS:"
    echo "   sudo CALLBACK_QAS='https://...' $0 aplicar"
    echo "Ele precisa ser https, sem usuario, sem query e sem fragmento - o codigo recusa"
    echo "qualquer outra coisa com MM_CALLBACK_INVALID."
    CONCLUIDO=1; exit 1; }
  case "$CALLBACK_QAS" in
    https://*) ;;
    *) echo "O CALLBACK_QAS tem de comecar com https://. Nada foi alterado."; CONCLUIDO=1; exit 1 ;;
  esac
  case "$CALLBACK_QAS" in
    *\?*|*\#*|*@*) echo "O CALLBACK_QAS nao pode ter query, fragmento nem usuario."; CONCLUIDO=1; exit 1 ;;
  esac

  REL="$(release_do_qas)"
  [ -n "$REL" ] || { echo "Nao achei o release do $QAS_MT. Ele esta no ar?"; CONCLUIDO=1; exit 1; }

  # O BINARIO PRECISA DERIVAR O CAMINHO. Sem a correcao publicada, este motor leria o
  # runtime.json do DEV e gravaria em fugates_dev - com credencial valida e sem erro nenhum.
  if ! grep -qa 'qas120/mm-runtime.json' "$REL/FugaTES.HostIntegracao.dll" 2>/dev/null; then
    echo "O binario em $REL nao conhece /etc/fugates/qas120/mm-runtime.json."
    echo "Ele leria a configuracao do DEV e gravaria no banco do DEV. Publique o motor novo"
    echo "antes de aplicar. Nada foi alterado."
    CONCLUIDO=1; exit 1
  fi
  echo "== o binario deriva o caminho do runtime pelo ambiente"

  U="$(systemctl show "$QAS_MT" -p User --value 2>/dev/null)"; [ -n "$U" ] || U=fugates-http
  install -d -m 0750 "$ENV_QAS"; chown root:"$U" "$ENV_QAS" 2>/dev/null || true
  install -d -m 0750 "$MM_DIR"; chown "$U":"$U" "$MM_DIR" 2>/dev/null || true

  if [ -f "$RUNTIME" ]; then
    echo "== $RUNTIME ja existe, preservado"
  else
    cat > "$RUNTIME" <<EOF
{
  "MmPurchaseOrders": {
    "Enabled": true,
    "MasterdataValidationEnabled": true,
    "RetryCommandsEnabled": false,
    "IntervalSeconds": $INTERVALO,
    "CreationCutoff": "$CORTE",
    "Host": "127.0.0.1",
    "Port": 5433,
    "SapOrigin": "$SAP_QAS_URL",
    "SapClient": "$SAP_QAS_CLI",
    "Database": "$BANCO_QAS",
    "Role": "$PAPEL_MM",
    "StateDirectory": "$MM_DIR",
    "CallbackUrl": "$CALLBACK_QAS",
    "ActivationReference": "$ATIVACAO",
    "WindowsCutoverReference": "$CORTE_WINDOWS",
    "WindowsCollectorDisabled": true
  }
}
EOF
    chmod 0640 "$RUNTIME"; chown root:"$U" "$RUNTIME" 2>/dev/null || true
    echo "== $RUNTIME criado"
  fi

  # O PAPEL DE MM PRECISA ESTAR NO PASSFILE, como o ACM e o SD.
  if ! grep -qE "^127\.0\.0\.1:5433:$BANCO_QAS:$PAPEL_MM:" "$ENV_QAS/pg.pass" 2>/dev/null; then
    echo "O $ENV_QAS/pg.pass nao tem linha para $PAPEL_MM."
    echo "Acrescente, sem a senha ficar no historico:"
    echo "   read -rs -p 'senha: ' S && printf '127.0.0.1:5433:$BANCO_QAS:$PAPEL_MM:%s\\n' \"\$S\" | sudo tee -a $ENV_QAS/pg.pass > /dev/null; unset S"
    echo "Nada foi alterado."
    CONCLUIDO=1; exit 1
  fi
  sudo -u "$U" test -r "$ENV_QAS/pg.pass" 2>/dev/null || {
    echo "O usuario $U nao consegue ler $ENV_QAS/pg.pass - confira a travessia do diretorio."
    CONCLUIDO=1; exit 1; }
  echo "== passfile tem a linha do papel MM do QAS e o servico consegue le-la"

  # A UNIDADE E A DO DEV COPIADA, com o Mode e as credenciais que ela ja declara. O texto, e nao
  # o relatorio: foi assim que o LoadCredential apareceu nos outros servicos.
  herdadas="$(systemctl cat "$DEV_MM" 2>/dev/null \
    | awk '/^\[Service\]/{d=1;next} /^\[/{d=0} d' \
    | grep -vE '^[[:space:]]*([#;]|$)' \
    | grep -vE '^[[:space:]]*(ExecStart|ExecStartPre|EnvironmentFile|WorkingDirectory|SyslogIdentifier|SetCredential|User|Group|Environment=HostIntegracao__Mode)[[:space:]]*=')"

  {
    echo "[Unit]"
    echo "Description=FugaTES - motor de pedidos de compra MM, ambiente QAS120"
    echo "After=network-online.target postgresql.service"
    echo "Wants=network-online.target"
    echo
    echo "[Service]"
    printf '%s\n' "$herdadas"
    echo "Environment=HostIntegracao__Mode=MmPurchaseOrders"
    echo "Environment=Sap__BaseUrl=$SAP_QAS_URL"
    echo "Environment=Sap__SapClient=$SAP_QAS_CLI"
    echo "User=$U"
    echo "Group=$U"
    echo "WorkingDirectory=$REL"
    echo "ExecStartPre=/usr/bin/test -r $RUNTIME"
    echo "ExecStart=/usr/bin/dotnet $REL/FugaTES.HostIntegracao.dll"
    echo "SyslogIdentifier=fugates-mm-qas120"
    echo
    echo "[Install]"
    echo "WantedBy=multi-user.target"
  } > "$UNIDADES/$UNIDADE.service"
  chmod 0644 "$UNIDADES/$UNIDADE.service"

  # O Sap__BaseUrl TEM de estar na unidade: e dele que sai o caminho do runtime.json. Sem ele o
  # motor cairia no DEV110 por omissao - e leria a configuracao do DEV.
  grep -q "^Environment=Sap__BaseUrl=$SAP_QAS_URL\$" "$UNIDADES/$UNIDADE.service" || {
    echo "A unidade ficou sem Sap__BaseUrl do QAS - ela leria a configuracao do DEV."
    CONCLUIDO=1; exit 1; }

  echo "== unidade escrita, para voce conferir:"
  sed 's/^/      /' "$UNIDADES/$UNIDADE.service"

  systemctl daemon-reload
  systemctl enable "$UNIDADE" >/dev/null 2>&1 || true
  systemctl restart "$UNIDADE" || echo "   o systemd recusou subir $UNIDADE"
  sleep 6

  echo
  echo "== estado"
  printf '  %-30s %s\n' "$UNIDADE" "$(ativo "$UNIDADE")"
  printf '  %-30s %s\n' "$DEV_MM" "$(ativo "$DEV_MM")   (o do DEV)"
  echo
  echo "== o snapshot do QAS foi escrito?"
  if [ -n "$(ls -A "$MM_DIR" 2>/dev/null)" ]; then
    ls -l "$MM_DIR" | sed 's/^/   /'
  else
    echo "   ainda vazio. O primeiro ciclo leva ate $((INTERVALO/60)) minutos; veja o journal:"
    echo "      sudo journalctl -u $UNIDADE -n 30 --no-pager"
  fi
  [ "$(ativo "$UNIDADE")" = active ] || {
    echo "O $UNIDADE nao esta ativo:  sudo journalctl -u $UNIDADE -n 40 --no-pager"
    CONCLUIDO=1; exit 1; }
  echo
  echo "Pronto. A tela de Pedidos de Compra do desktop do QAS deve sair de"
  echo "\"Snapshot MM indisponivel\" apos o primeiro ciclo."
  CONCLUIDO=1
  ;;

reverter)
  printf 'Isto para e remove o motor MM do QAS120. Escreva: REVERTER MM\n> '
  read -r r
  [ "$r" = "REVERTER MM" ] || { echo "Nada foi alterado."; CONCLUIDO=1; exit 1; }
  systemctl disable --now "$UNIDADE" >/dev/null 2>&1 || true
  rm -f "$UNIDADES/$UNIDADE.service"
  systemctl daemon-reload
  echo "Unidade removida. O $RUNTIME e o $MM_DIR ficaram - apague a mao se quiser."
  CONCLUIDO=1
  ;;

*)
  echo "Uso: sudo CALLBACK_QAS='https://...' $0 {conferir|aplicar|reverter}"
  CONCLUIDO=1; exit 1
  ;;
esac
