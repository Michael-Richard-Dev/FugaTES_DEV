#!/usr/bin/env bash
# Publica o release do motor ACM com o CORTE do cadastro automatico e liga o corte - as duas coisas
# num passo so, pelo mesmo motivo de sempre: separar a troca de release da configuracao foi o que,
# em 04/11/2026, permitiu que um script anunciasse sucesso sobre um arquivo que o systemd ignorava.
#
# O QUE O CORTE FAZ. So contrato RECEBIDO a partir de um instante vira cadastro automatico. Pedido
# do operador em 05/11/2026: "daqui pra frente, dos proximos que vierem". A fila tinha cerca de
# quarenta contratos travados - uns em reconciliacao com o Guardian, uns em recusa 400, uns em
# aprovacao de callback - e ele quis que o motor deixasse de persegui-los.
#
# E corte por DATA DE RECEBIMENTO, nao lista de numeros: o pedido era temporal. Enumerar os
# quarenta satisfaria hoje e exigiria manutencao amanha; o corte diz a regra uma vez. Contrato
# antigo que o SAP REENTREGAR depois do corte entra - o recebimento e novo, ainda que o numero
# seja antigo. E o que "os proximos que vierem" quer dizer.
#
# O corte e um "e" a mais, nunca um atalho: centro, par tipo+pesagem, status T e versao 1 continuam
# valendo, e identificador sem prova no Guardian continua bloqueando antes do envio.
#
# O INSTANTE NAO E ESCOLHIDO DE CABECA. O script le o recebimento mais recente que existe no banco,
# adota o momento da execucao como corte, e se RECUSA a seguir se o momento nao for estritamente
# maior do que esse recebimento. Assim o corte e provadamente capaz de excluir a fila inteira, em
# vez de depender de uma constante que alguem transcreveu de um terminal.
#
# Uso:  sudo bash publicar-corte-acm.sh conferir  [--pacote ~/r173]
#       sudo bash publicar-corte-acm.sh instalar  [--pacote ~/r173] [--release R173_CORTE_20261105]
#       sudo bash publicar-corte-acm.sh reverter
set -euo pipefail

MODO=${1:-}
shift || true
ORIGEM=/home/michael/r173
NOME=R173_CORTE_20261105
while [ $# -gt 0 ]; do
  case "$1" in
    --pacote)  ORIGEM=${2:-}; shift 2 ;;
    --release) NOME=${2:-}; shift 2 ;;
    *) echo "BLOCKED: argumento desconhecido: $1"; exit 1 ;;
  esac
done
echo "$NOME" | grep -qE '^[A-Za-z0-9_]+$' || { echo "BLOCKED: nome de release invalido: '$NOME'"; exit 1; }

SERVICO=fugates-acm-motor-dev
RELEASES=/opt/fugates/releases/FugaTES.ACM
DESTINO=$RELEASES/$NOME/runtime/host
ENVDIR=/etc/fugates/motor-dev
ENVCORTE=$ENVDIR/corte.env
DROPDIR=/etc/systemd/system/$SERVICO.service.d
PGPASS=/home/michael/.fugates-acm-r165p/pgpass
SELO=$(date -u +%Y%m%d_%H%M%SZ)
MARCAS=(RecebidoDesdeUtc RecebimentoNoCorte CentrosEfetivos)

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }
case "$MODO" in conferir|instalar|reverter) ;; *) echo "uso: sudo bash $0 conferir|instalar|reverter"; exit 1 ;; esac

contar() { tr -d '\000' < "$1" | grep -c "$2" || true; }
efetivo() { systemctl show -p "$1" --value "$SERVICO" 2>/dev/null; }
consulta() {
  sudo -u michael env PGPASSFILE=$PGPASS psql -w \
    'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc "$1"
}

binding() {
  local f ultimo=
  for f in "$DROPDIR"/*.conf; do
    [ -f "$f" ] || continue
    grep -qE '^[[:space:]]*ExecStart=[^[:space:]]' "$f" && ultimo=$f
  done
  echo "$ultimo"
}
BINDING=$(binding)

# ---------------------------------------------------------------- reverter
if [ "$MODO" = reverter ]; then
  test -n "$BINDING" || { echo "BLOCKED: nenhum drop-in com ExecStart em $DROPDIR"; exit 1; }
  ULTIMO=$(ls -1t "$BINDING".bak.corte.* 2>/dev/null | head -1 || true)
  test -n "$ULTIMO" || { echo "BLOCKED: nenhum backup .bak.corte.* de $BINDING"; exit 1; }
  cp "$ULTIMO" "$BINDING"
  rm -f "$ENVCORTE"
  systemctl daemon-reload
  systemctl restart "$SERVICO"
  sleep 6
  echo "-- binding restaurado de $ULTIMO; $ENVCORTE removido"
  echo "-- ExecStart efetivo: $(efetivo ExecStart | sed 's/.*argv\[\]=//; s/ ;.*//')"
  systemctl is-active --quiet "$SERVICO" && echo "-- motor ATIVO sem corte; a fila antiga volta ao recorte" \
    || { echo "   BLOCKED: motor nao subiu apos reverter"; journalctl -u "$SERVICO" --since "-2 min" --no-pager -q | tail -8 | sed 's/^/     /'; exit 1; }
  exit 0
fi

# ---------------------------------------------------------------- estado
echo "=== estado efetivo do $SERVICO ==="
WD_ATUAL=$(efetivo WorkingDirectory)
echo "   binding canonico ... ${BINDING:-(nenhum)}"
echo "   WorkingDirectory ... ${WD_ATUAL:-(vazio)}"
echo "   ExecStart .......... $(efetivo ExecStart | sed 's/.*argv\[\]=//; s/ ;.*//')"
test -n "$BINDING" || { echo "BLOCKED: nenhum drop-in define ExecStart"; exit 1; }
test -d "${WD_ATUAL:-/dev/null}" || { echo "BLOCKED: WorkingDirectory efetivo nao existe"; exit 1; }

echo
echo "=== corte declarado hoje ==="
grep -rn "MotorIntegracao__AcmAutomatico__RecebidoDesdeUtc=" "$ENVDIR"/*.env 2>/dev/null | sed 's|^|   |' \
  || echo "   (nenhum corte declarado - o motor varre a fila inteira)"

# ---------------------------------------------------------------- o pacote
echo
echo "=== pacote ==="
FONTE=$ORIGEM/runtime/host
test -f "$FONTE/FugaTES.Aplicacao.dll" || { echo "   BLOCKED: pacote ausente em $FONTE"; exit 1; }
echo "   origem ............. $FONTE ($(ls -1 "$FONTE" | wc -l) itens)"
for M in "${MARCAS[@]}"; do
  N=$(contar "$FONTE/FugaTES.Aplicacao.dll" "$M")
  printf '   %-20s %s\n' "$M" "$N"
  test "$N" -ge 1 || { echo "   BLOCKED: o pacote nao contem '$M'; nao e o binario do corte"; exit 1; }
done
FALTANDO=$(comm -13 <(ls -1 "$FONTE" | sort) <(ls -1 "$WD_ATUAL" | sort))
test -z "$FALTANDO" || { echo "   BLOCKED: itens do release EM USO ausentes no pacote:"; echo "$FALTANDO" | sed 's/^/     /'; exit 1; }
echo "   nada do release em uso ficou de fora  OK"

# ---------------------------------------------------------------- o instante do corte
echo
echo "=== o corte ==="
ULTIMO_RECEBIMENTO=$(consulta "select coalesce(to_char(max(criado_em) at time zone 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.USZ'),'') from fugates.contrato_sap_toledo_versao" | tr -d '[:space:]')
QUANTOS=$(consulta "select count(*) from fugates.contrato_sap_toledo_versao" | tr -d '[:space:]')
CORTE=$(date -u +%Y-%m-%dT%H:%M:%S.%6NZ)
echo "   versoes de contrato no banco ....... ${QUANTOS:-?}"
echo "   recebimento mais recente ........... ${ULTIMO_RECEBIMENTO:-(nenhum)}"
echo "   corte a declarar ................... $CORTE"

if [ -n "$ULTIMO_RECEBIMENTO" ]; then
  # Comparacao feita pelo PostgreSQL, que entende timestamptz - nao por string.
  MAIOR=$(consulta "select case when '$CORTE'::timestamptz > '$ULTIMO_RECEBIMENTO'::timestamptz then 'sim' else 'nao' end" | tr -d '[:space:]')
  if [ "$MAIOR" != "sim" ]; then
    echo "   BLOCKED: o corte NAO e posterior ao recebimento mais recente."
    echo "            Com isso ele nao excluiria a fila atual, que e o pedido. Possivel"
    echo "            diferenca de relogio entre esta maquina e o banco. Nada foi alterado."
    exit 1
  fi
  echo "   o corte e posterior a tudo que ja chegou  OK"
  FORA=$(consulta "select count(*) from fugates.contrato_sap_toledo_versao where criado_em < '$CORTE'::timestamptz" | tr -d '[:space:]')
  echo "   versoes que SAIRAO do recorte ...... ${FORA:-?}"
fi

if [ "$MODO" = conferir ]; then
  echo
  echo "Nada foi alterado. Se o acima estiver certo:  sudo bash $0 instalar"
  exit 0
fi

# ---------------------------------------------------------------- instalar
echo
echo "=== instalando ==="
systemctl is-active --quiet "$SERVICO" && { echo "-- parando o motor"; systemctl stop "$SERVICO"; }

[ -d "$DESTINO" ] && rm -rf "$DESTINO"
install -d -o root -g root -m 755 "$DESTINO"
cp -a "$FONTE/." "$DESTINO/"
chown -R root:root "$RELEASES/$NOME"
echo "-- release em disco: $DESTINO ($(ls -1 "$DESTINO" | wc -l) itens)"

cp "$BINDING" "$BINDING.bak.corte.$SELO"
echo "-- backup do binding: $BINDING.bak.corte.$SELO"

sed -i "s|${WD_ATUAL}|${DESTINO}|g" "$BINDING"
grep -q "$DESTINO" "$BINDING" || { echo "BLOCKED: o drop-in nao foi reapontado"; cp "$BINDING.bak.corte.$SELO" "$BINDING"; exit 1; }

cat > "$ENVCORTE" <<EOF
# Corte do cadastro automatico do ACM. Escrito por publicar-corte-acm.sh em $SELO.
#
# So contrato RECEBIDO a partir deste instante vira cadastro automatico. Pedido do operador em
# 05/11/2026: "daqui pra frente, dos proximos que vierem".
#
# Conferido na hora de escrever: o recebimento mais recente no banco era
#   ${ULTIMO_RECEBIMENTO:-(nenhum)}
# e este corte e posterior a ele, entao a fila que existia sai do recorte por inteiro.
#
# Instante FIXO, nunca "agora": com corte movel, o contrato que chegou hoje e ainda nao foi
# cadastrado sairia do recorte amanha, sem ninguem decidir isso.
#
# Carregado como ULTIMO EnvironmentFile do drop-in canonico, pela mesma razao dos outros:
# EnvironmentFile acumula e o ultimo vence por chave.
MotorIntegracao__AcmAutomatico__RecebidoDesdeUtc=$CORTE
EOF
chown root:michael "$ENVCORTE"; chmod 640 "$ENVCORTE"
echo "-- $ENVCORTE criado com o corte $CORTE"

grep -qF "EnvironmentFile=$ENVCORTE" "$BINDING" || printf 'EnvironmentFile=%s\n' "$ENVCORTE" >> "$BINDING"
echo "-- ligado como ultimo EnvironmentFile de $(basename "$BINDING")"

restaurar() {
  cp "$BINDING.bak.corte.$SELO" "$BINDING"
  rm -f "$ENVCORTE"
  systemctl daemon-reload
  systemctl start "$SERVICO" || true
}

systemctl daemon-reload
systemctl start "$SERVICO"
sleep 6

EXEC_NOVO=$(efetivo ExecStart | sed 's/.*argv\[\]=//; s/ ;.*//')
WD_NOVO=$(efetivo WorkingDirectory)
echo
echo "-- ExecStart efetivo: $EXEC_NOVO"
case "$EXEC_NOVO" in
  *"$DESTINO"*) echo "-- o systemd resolveu para o release novo  OK" ;;
  *) echo "   BLOCKED: o ExecStart efetivo NAO e o release novo. Revertendo."; restaurar; exit 1 ;;
esac
for M in "${MARCAS[@]}"; do
  N=$(contar "$WD_NOVO/FugaTES.Aplicacao.dll" "$M")
  test "$N" -ge 1 || { echo "   BLOCKED: o binario em execucao nao contem '$M'. Revertendo."; restaurar; exit 1; }
done
echo "-- o binario em execucao conhece o corte  OK"

systemctl is-active --quiet "$SERVICO" || {
  echo "   BLOCKED: o motor nao subiu. Ultimas linhas:"
  journalctl -u "$SERVICO" --since "-2 min" --no-pager -q 2>/dev/null \
    | grep -iE "exception|fail|erro|invalid|required|scope" | tail -10 | sed 's/^/     /'
  restaurar; exit 1; }
echo "-- motor ATIVO"

# Conferencia do EFEITO, no processo e nao no arquivo.
PID=$(systemctl show -p MainPID --value "$SERVICO")
if [ -n "$PID" ] && [ "$PID" != 0 ] && [ -r "/proc/$PID/environ" ]; then
  tr '\0' '\n' < "/proc/$PID/environ" | grep -qx "MotorIntegracao__AcmAutomatico__RecebidoDesdeUtc=$CORTE" \
    && echo "-- o processo em execucao ve o corte  OK" \
    || { echo "   BLOCKED: o processo NAO ve o corte. Revertendo."; restaurar; exit 1; }
fi

echo
echo "-- aguardando dois ciclos do motor (60s)..."
sleep 60
echo
echo "=== a fila que ficou do lado de fora do corte ==="
# ATENCAO ao ler o segundo numero. Ele conta integracoes em estado que a CONSULTA do motor leria;
# nao e numero de tentativa. Quem as descarta e o RECORTE, um passo depois da consulta - e e por
# isso que o resultado do ciclo e NenhumTrabalho em vez de tentativa falhando. A prova de que
# pararam de ser tentadas e o journal abaixo, nao este numero.
consulta "select 'antes do corte: '||count(*)||' integracao(oes), '||count(*) filter (where i.estado in ('PENDENTE','AGUARDANDO_REPROCESSAMENTO'))||' em estado que a consulta leria - todas descartadas pelo recorte'
          from fugates.integracao i join fugates.contrato_sap_toledo_versao v on v.integracao_id=i.id
          where i.operacao='SAP_CONTRATO_TOLEDO' and v.criado_em < '$CORTE'::timestamptz" | sed 's/^/   /' || true
echo
echo "=== a prova: o ciclo do motor nao acha trabalho ==="
journalctl -u "$SERVICO" --since "-90 s" --no-pager -q 2>/dev/null | tail -12 | sed 's/^/   /' || true

echo
echo "Daqui em diante, so contrato recebido depois de $CORTE vira cadastro automatico."
echo "Para desfazer: sudo bash $0 reverter"
