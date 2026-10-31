#!/usr/bin/env bash
# R170 - Troca o release do motor continuo, mantendo configuracao, mapeamentos e escopo.
#
# Generico de proposito: descobre o release em uso lendo a propria unit, instala o novo ao lado
# (nada e sobrescrito) e reaponta. A reversao volta para o release que estava em uso antes.
#
# Uso:  sudo bash atualizar-motor-r170.sh instalar <nome-do-release> [/caminho/do/pacote]
#       sudo bash atualizar-motor-r170.sh reverter
set -euo pipefail

MODO=${1:-}
NOME=${2:-}
ORIGEM=${3:-/home/michael/r170r}
RELEASES=/opt/fugates/releases/FugaTES.ACM
SERVICO=fugates-acm-motor-dev
UNIT=/etc/systemd/system/$SERVICO.service
PGPASS=/home/michael/.fugates-acm-r165p/pgpass
SELO=$(date -u +%Y%m%d_%H%M%SZ)

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }

atual() { grep -E "^WorkingDirectory=" "$UNIT" | head -1 | cut -d= -f2-; }

if [ "$MODO" = reverter ]; then
  ULTIMO=$(ls -1t "$UNIT".bak.upd.* 2>/dev/null | head -1 || true)
  test -n "$ULTIMO" || { echo "BLOCKED: nenhum backup de unit desta operacao"; exit 1; }
  systemctl stop "$SERVICO" 2>/dev/null || true
  cp "$ULTIMO" "$UNIT"
  systemctl daemon-reload
  systemctl start "$SERVICO"
  sleep 5
  systemctl is-active --quiet "$SERVICO" || { echo "BLOCKED: motor nao subiu apos reverter"; exit 1; }
  echo "-- revertido para: $(atual)"
  exit 0
fi

test "$MODO" = instalar || { echo "uso: sudo bash $0 instalar <nome-do-release> [pacote]"; echo "     sudo bash $0 reverter"; exit 1; }
echo "$NOME" | grep -qE '^[A-Za-z0-9_]+$' || { echo "BLOCKED: nome de release invalido: '$NOME'"; exit 1; }

ATUAL=$(atual)
test -n "$ATUAL" || { echo "BLOCKED: nao consegui ler o release em uso da unit"; exit 1; }
NOVO=$RELEASES/$NOME
test "$NOVO/runtime/host" != "$ATUAL" || { echo "BLOCKED: o release '$NOME' ja e o que esta em uso"; exit 1; }

FONTE=$ORIGEM/runtime/host/FugaTES.Aplicacao.dll
test -f "$FONTE" || { echo "BLOCKED: pacote ausente em $ORIGEM/runtime/host"; exit 1; }

echo "=== atualizacao do motor continuo ==="
echo "-- em uso: $ATUAL"
echo "-- novo:   $NOVO/runtime/host"

systemctl is-active --quiet "$SERVICO" && { echo "-- parando o motor"; systemctl stop "$SERVICO"; }

[ -d "$NOVO/runtime/host" ] && rm -rf "$NOVO/runtime/host"
install -d -o root -g root -m 755 "$NOVO/runtime/host"
cp -a "$ORIGEM/runtime/host/." "$NOVO/runtime/host/"
chown -R root:root "$NOVO"

FALTANDO=$(comm -13 <(ls -1 "$NOVO/runtime/host" | sort) <(ls -1 "$ATUAL" | sort))
test -z "$FALTANDO" || { echo "   BLOCKED: arquivos do release em uso ausentes no novo:"; echo "$FALTANDO"; exit 1; }
echo "-- instalado ($(ls -1 "$NOVO/runtime/host" | wc -l) arquivos); nada do release anterior ficou de fora"

cp "$UNIT" "$UNIT.bak.upd.$SELO"
sed -i "s|${ATUAL}|${NOVO}/runtime/host|g" "$UNIT"
grep -q "$NOVO/runtime/host" "$UNIT" || { echo "BLOCKED: unit nao reapontada"; cp "$UNIT.bak.upd.$SELO" "$UNIT"; exit 1; }
echo "-- backup da unit: $UNIT.bak.upd.$SELO"

systemctl daemon-reload
systemctl start "$SERVICO"
sleep 5
if ! systemctl is-active --quiet "$SERVICO"; then
  echo "   BLOCKED: o motor nao subiu. Revertendo a unit."
  cp "$UNIT.bak.upd.$SELO" "$UNIT"; systemctl daemon-reload; systemctl start "$SERVICO" || true
  journalctl -u "$SERVICO" --since "-2 min" --no-pager -q 2>/dev/null \
    | grep -iE "exception|fail|erro|exige|invalid|required" | tail -8 | sed 's/^/     /'
  exit 1
fi
echo "-- motor ATIVO em $(atual)"

echo "-- aguardando o motor processar a fila (40s)..."
sleep 40
echo
sudo -u michael env PGPASSFILE=$PGPASS psql -w \
  'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc \
  "select i.chave_negocio||'  '||i.estado||'  '||coalesce(p.etapa_funcional,'(sem etapa)')||'  '||coalesce(p.erro_codigo,'-')||coalesce('  seq='||nullif(split_part(p.resultado_funcional,':',4),''),'') from fugates.integracao i left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id where i.operacao='SAP_CONTRATO_TOLEDO' order by 1" \
  | sed 's/^/   /' || true
echo
echo "Para reverter: sudo bash $0 reverter"
