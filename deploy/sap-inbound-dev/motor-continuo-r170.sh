#!/usr/bin/env bash
# R170 - Deixa o motor FugaTES permanentemente ativo, cadastrando no Guardian os contratos que o
# SAP entregar, sem intervencao manual.
#
# Por que nao pela janela de admissao: a janela so admite contratos que CHEGAM com ela aberta,
# porque exige correlacao carimbada com o prefixo da janela. O receptor so carimba quando roda em
# SecurityMode=LoopbackBasicHttps, que por sua vez exige X-Forwarded-Proto: https - ou seja, o SAP
# teria de passar a entregar por HTTPS. O receptor hoje esta em LoopbackOnly e a entrega e HTTP.
# Receptor e SAP ficam INTOCADOS por este script.
#
# O recorte usado e o continuo (MotorIntegracao__AcmAutomatico), que aplica a mesma regra de
# negocio da janela - centro 3011, status T, pares fechados compra AC3P/E/10 e venda AC3S/S/40,
# historicos excluidos - sem caixa de tempo e sem prefixo de correlacao.
#
# Identificadores sao FAIL-CLOSED: so cadastra contrato cujo parceiro e material tenham codigo
# Guardian aprovado abaixo. Qualquer outro e bloqueado ANTES do envio e segue visivel no FugaTES.
#
# Uso:  sudo bash motor-continuo-r170.sh instalar
#       sudo bash motor-continuo-r170.sh parar
#       sudo bash motor-continuo-r170.sh reverter
set -euo pipefail

MODO=${1:-}
ORIGEM=/home/michael/r170c
RELEASES=/opt/fugates/releases/FugaTES.ACM
NOVO=$RELEASES/R170_CONTINUO_20261029_R1
ANTERIOR=$RELEASES/R170_VENDA_20261029_R1
SERVICO=fugates-acm-motor-dev
UNIT=/etc/systemd/system/$SERVICO.service
ENVDIR=/etc/fugates/motor-dev
ENVR57=$ENVDIR/r57.env
ENVESCOPO=$ENVDIR/escopo.env
SELO=$(date -u +%Y%m%d_%H%M%SZ)

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }
case "$MODO" in instalar|parar|reverter) ;; *) echo "uso: sudo bash $0 instalar|parar|reverter"; exit 1 ;; esac

contar() { tr -d '\000' < "$1" | grep -c "$2" || true; }

# ---------------------------------------------------------------- parar
if [ "$MODO" = parar ]; then
  systemctl disable --now "$SERVICO" 2>/dev/null || true
  printf '# motor parado manualmente em %s\n' "$SELO" > "$ENVESCOPO"
  chown root:michael "$ENVESCOPO"; chmod 640 "$ENVESCOPO"
  systemctl daemon-reload
  echo "Motor parado e desabilitado na inicializacao. Nada mais sera cadastrado."
  exit 0
fi

# ---------------------------------------------------------------- reverter
if [ "$MODO" = reverter ]; then
  systemctl disable --now "$SERVICO" 2>/dev/null || true
  ULTIMO=$(ls -1t "$UNIT".bak.r170c.* 2>/dev/null | head -1 || true)
  if [ -n "$ULTIMO" ]; then cp "$ULTIMO" "$UNIT"; echo "-- unit restaurada de $ULTIMO"
  else sed -i "s|$NOVO/runtime/host|$ANTERIOR/runtime/host|g" "$UNIT"; echo "-- unit reapontada para $ANTERIOR"; fi
  printf '# revertido em %s\n' "$SELO" > "$ENVESCOPO"
  printf '# revertido em %s\n' "$SELO" > "$ENVR57"
  chown root:michael "$ENVESCOPO" "$ENVR57"; chmod 640 "$ENVESCOPO" "$ENVR57"
  systemctl daemon-reload
  grep -E "^(WorkingDirectory|ExecStart)=" "$UNIT" | sed 's/^/   /'
  echo "Revertido. O motor esta parado e desabilitado; os releases permanecem em disco."
  exit 0
fi

# ---------------------------------------------------------------- instalar
echo "=== R170 - motor continuo ==="

FONTE=$ORIGEM/runtime/host/FugaTES.Aplicacao.dll
test -f "$FONTE" || { echo "BLOCKED: pacote ausente em $ORIGEM/runtime/host"; exit 1; }
echo
echo "-- verificacao do pacote:"
for S in AC3S AcmAutomatico ApprovedPartnerMappings R170_PARTNER_MAPPING_REQUIRED; do
  N=$(contar "$FONTE" "$S"); printf '   %-32s %s\n' "$S" "$N"
  test "$N" -ge 1 || { echo "   BLOCKED: pacote nao contem $S; nao e o binario R170 continuo"; exit 1; }
done

systemctl is-active --quiet "$SERVICO" && { echo; echo "-- motor ativo; parando antes da troca"; systemctl stop "$SERVICO"; }

echo
[ -d "$NOVO/runtime/host" ] && rm -rf "$NOVO/runtime/host"
install -d -o root -g root -m 755 "$NOVO/runtime/host"
cp -a "$ORIGEM/runtime/host/." "$NOVO/runtime/host/"
chown -R root:root "$NOVO"
echo "-- release instalado: $NOVO/runtime/host ($(ls -1 "$NOVO/runtime/host" | wc -l) arquivos)"
FALTANDO=$(comm -13 <(ls -1 "$NOVO/runtime/host" | sort) <(ls -1 "$ANTERIOR/runtime/host" | sort))
test -z "$FALTANDO" || { echo "   BLOCKED: arquivos do release anterior ausentes no novo:"; echo "$FALTANDO"; exit 1; }
echo "-- nenhum arquivo do release anterior ficou de fora  OK"

# Governanca e mapeamento. Referencias apontam para
# docs/Coordination/EVIDENCIAS_R57_CONTRATO_1160.md e docs/Coordination/MOTOR_CONTINUO_R170.md.
cat > "$ENVR57" <<EOF
# R170 - governanca do cadastro continuo.
AcmR57__Normal__AuthorizationReference=R170-OPERADOR-CONTINUO
AcmR57__Normal__GuardianIdentityEvidence=R169-GUARDIAN-IDENTITY-20261029
AcmR57__Normal__PostgreSqlIdentityEvidence=R169-POSTGRESQL-IDENTITY-20261029
AcmR57__Normal__JournalEvidence=R169-JOURNAL-20261029
AcmR57__Normal__IdentifierMappingEvidence=R170-MAPEAMENTO-20261029
AcmR57__Normal__ProximityValueEvidence=R169-PROXIMIDADE-20261029
AcmR57__Normal__ValorProximidadeFimContrato=0
# Sem janela: as aprovacoes verbatim ficam todas FALSE. Quem resolve emissor e item e o par
# ApprovedPartnerMappings + ApprovedMaterialMappings abaixo.
AcmR57__Normal__SapContractCopiedVerbatimApproved=false
AcmR57__Normal__SapIdentifiersCopiedVerbatimApproved=false
AcmR57__Normal__SapContractAndMaterialCopiedVerbatimApproved=false
# Mapeamento SAP -> Guardian, chaveado por centro + identificador, nas formas GRAVADAS no banco.
# Comprovado no cadastro do contrato 0000001160 (sequencial 588) e conferido na tela do CloudPrix.
# ACRESCENTE AQUI novos pares assim que o codigo Guardian for confirmado; sem entrada, o contrato
# e bloqueado antes do envio e permanece visivel no FugaTES.
AcmR57__Normal__ApprovedPartnerMappings__0__Centro=3011
AcmR57__Normal__ApprovedPartnerMappings__0__SapPartnerId=0020000051
AcmR57__Normal__ApprovedPartnerMappings__0__GuardianCode=20000051
AcmR57__Normal__ApprovedPartnerMappings__0__EvidenceReference=R170-MAPEAMENTO-20261029
AcmR57__Normal__ApprovedMaterialMappings__0__Centro=3011
AcmR57__Normal__ApprovedMaterialMappings__0__SapMaterial=000000000007000002
AcmR57__Normal__ApprovedMaterialMappings__0__GuardianCode=7000000
AcmR57__Normal__ApprovedMaterialMappings__0__EvidenceReference=R170-MAPEAMENTO-20261029
# Recorte continuo. A janela fica desligada: ver o cabecalho deste script.
MotorIntegracao__AcmInboundWindow__Enabled=false
MotorIntegracao__AcmAutomatico__Enabled=true
MotorIntegracao__AcmAutomatico__AuthorizationReference=R170-OPERADOR-CONTINUO
MotorIntegracao__AcmAutomatico__Centro=3011
EOF
chown root:michael "$ENVR57"; chmod 640 "$ENVR57"

# O ExecStartPre da unit exige este marcador. Ele continua sendo a trava de partida: zerar este
# arquivo impede o motor de subir.
printf 'MOTOR_MODO_AUTOMATICO=1\n' > "$ENVESCOPO"
chown root:michael "$ENVESCOPO"; chmod 640 "$ENVESCOPO"

cp "$UNIT" "$UNIT.bak.r170c.$SELO"
sed -i "s|$ANTERIOR/runtime/host|$NOVO/runtime/host|g" "$UNIT"
grep -q "$NOVO/runtime/host" "$UNIT" || { echo "BLOCKED: unit nao reapontada"; cp "$UNIT.bak.r170c.$SELO" "$UNIT"; exit 1; }
echo "-- backup da unit: $UNIT.bak.r170c.$SELO"

systemctl daemon-reload
systemctl enable "$SERVICO" >/dev/null 2>&1 || true
systemctl start "$SERVICO"
sleep 5
if ! systemctl is-active --quiet "$SERVICO"; then
  echo
  echo "   BLOCKED: o motor nao subiu. Ultimas linhas do log:"
  journalctl -u "$SERVICO" --since "-2 min" --no-pager -q 2>/dev/null \
    | grep -iE "exception|fail|erro|exige|invalid|required" | tail -8 | sed 's/^/     /'
  echo "   A unit foi reapontada mas o motor esta parado. Para desfazer: sudo bash $0 reverter"
  exit 1
fi

echo
echo "-- motor ATIVO e habilitado na inicializacao"
grep -E "^(WorkingDirectory|ExecStart)=" "$UNIT" | sed 's/^/   /'
echo
echo "Daqui em diante, contrato que o SAP entregar e que tenha mapeamento aprovado sera cadastrado"
echo "no Guardian sozinho. Sem mapeamento, fica bloqueado antes do envio e visivel no FugaTES."
echo "Para parar:   sudo bash $0 parar"
echo "Para reverter: sudo bash $0 reverter"
