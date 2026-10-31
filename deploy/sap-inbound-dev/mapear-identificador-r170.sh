#!/usr/bin/env bash
# R170 - Acrescenta um mapeamento SAP -> Guardian ao motor continuo.
#
# O motor so cadastra contrato cujo parceiro E material tenham codigo Guardian aprovado. Sem
# mapeamento ele bloqueia ANTES do envio (R170_PARTNER_MAPPING_REQUIRED / R170_MATERIAL_MAPPING_
# REQUIRED) e o contrato segue visivel no FugaTES. Este script e a forma de destravar.
#
# O identificador SAP usado e a forma GRAVADA NO BANCO, com os zeros a esquerda - nao a forma do
# OData. Confira em fugates.contrato_sap_toledo_versao antes de rodar.
#
# Uso:  sudo bash mapear-identificador-r170.sh parceiro 0020000184 20000184
#       sudo bash mapear-identificador-r170.sh material 000000000007000000 7000000
#       sudo bash mapear-identificador-r170.sh listar
set -euo pipefail

TIPO=${1:-}
SAP=${2:-}
GUARDIAN=${3:-}
ENVR57=/etc/fugates/motor-dev/r57.env
SERVICO=fugates-acm-motor-dev
PGPASS=/home/michael/.fugates-acm-r165p/pgpass
SELO=$(date -u +%Y%m%d_%H%M%SZ)

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }
test -f "$ENVR57" || { echo "BLOCKED: $ENVR57 nao existe; o motor continuo nao esta instalado"; exit 1; }

psql_acm() { sudo -u michael env PGPASSFILE=$PGPASS psql -w \
  'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc "$1" || true; }

mostrar() {
  echo "-- mapeamentos aprovados hoje:"
  grep -E "^AcmR57__Normal__Approved(Partner|Material)Mappings__[0-9]+__(SapPartnerId|SapMaterial|GuardianCode)=" "$ENVR57" \
    | sed 's/^AcmR57__Normal__Approved//' | sed 's/^/   /'
}

# Extrai o indice de uma chave. Fazer isto com "grep -oE [0-9]+" pegaria tambem o 57 de "AcmR57",
# e foi assim que um mapeamento acabou gravado no indice 58 em vez de 1.
indice_de() { echo "$1" | sed -E 's/^AcmR57__Normal__[A-Za-z]+__([0-9]+)__.*$/\1/'; }

# Reescreve um grupo com indices contiguos a partir de 0. Indices com buracos funcionam no binder,
# mas tornam o arquivo confuso de ler e de conferir.
renumerar() { # $1=GRUPO  $2=CHAVE
  local grupo=$1 chave=$2 tmp novo=0 idx
  tmp=$(mktemp)
  grep -vE "^(#.*->.*Guardian.*|AcmR57__Normal__${grupo}__[0-9]+__.*)$" "$ENVR57" > "$tmp" || true
  for idx in $(grep -oE "^AcmR57__Normal__${grupo}__[0-9]+__${chave}=" "$ENVR57" \
                 | sed -E "s/^AcmR57__Normal__${grupo}__([0-9]+)__${chave}=/\1/" | sort -n); do
    local sap code ev
    sap=$(grep -E "^AcmR57__Normal__${grupo}__${idx}__${chave}=" "$ENVR57" | head -1 | cut -d= -f2-)
    code=$(grep -E "^AcmR57__Normal__${grupo}__${idx}__GuardianCode=" "$ENVR57" | head -1 | cut -d= -f2-)
    ev=$(grep -E "^AcmR57__Normal__${grupo}__${idx}__EvidenceReference=" "$ENVR57" | head -1 | cut -d= -f2-)
    { echo "AcmR57__Normal__${grupo}__${novo}__Centro=3011"
      echo "AcmR57__Normal__${grupo}__${novo}__${chave}=${sap}"
      echo "AcmR57__Normal__${grupo}__${novo}__GuardianCode=${code}"
      echo "AcmR57__Normal__${grupo}__${novo}__EvidenceReference=${ev}"; } >> "$tmp"
    novo=$((novo + 1))
  done
  cat "$tmp" > "$ENVR57"
  rm -f "$tmp"
}

# O backoff e de 1 dia: um contrato que ja falhou por falta de mapeamento nao e reavaliado hoje so
# porque o mapeamento passou a existir. Reagendar apenas os bloqueados por mapeamento - nunca os
# terminais, os ja cadastrados ou os indeterminados.
reavaliar() {
  local n
  n=$(psql_acm "update fugates.integracao i set proxima_tentativa_em = clock_timestamp() from fugates.projecao_resultado_fluxo_mvp p where p.integracao_id = i.id and p.erro_codigo like 'R170_%_MAPPING_REQUIRED' and i.estado = 'AGUARDANDO_REPROCESSAMENTO' returning 1" | grep -c 1 || true)
  echo "-- reagendados para agora: ${n:-0} contrato(s) que estavam bloqueados por mapeamento"
}

if [ "$TIPO" = reavaliar ]; then
  cp "$ENVR57" "$ENVR57.bak.$SELO"
  renumerar ApprovedPartnerMappings SapPartnerId
  renumerar ApprovedMaterialMappings SapMaterial
  chown root:michael "$ENVR57"; chmod 640 "$ENVR57"
  echo "-- indices renumerados de forma contigua (backup: $ENVR57.bak.$SELO)"
  mostrar
  systemctl restart "$SERVICO"
  sleep 5
  systemctl is-active --quiet "$SERVICO" || {
    echo "   BLOCKED: motor nao subiu; restaurando"; cp "$ENVR57.bak.$SELO" "$ENVR57"
    systemctl restart "$SERVICO" || true; exit 1; }
  reavaliar
  echo "-- aguardando o motor processar a fila (30s)..."
  sleep 30
  echo
  psql_acm "select i.chave_negocio||'  '||i.estado||'  '||coalesce(p.etapa_funcional,'(sem etapa)')||'  '||coalesce(p.erro_codigo,'-')||coalesce('  seq='||nullif(split_part(p.resultado_funcional,':',4),''),'') from fugates.integracao i left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id where i.operacao='SAP_CONTRATO_TOLEDO' order by 1" | sed 's/^/   /'
  exit 0
fi

if [ "$TIPO" = listar ]; then
  mostrar
  echo
  echo "-- contratos bloqueados por falta de mapeamento:"
  psql_acm "select i.chave_negocio||'  '||p.erro_codigo||'  parceiro='||coalesce(w.payload_contrato->>'IdParceiro','?')||'  material='||coalesce(w.payload_contrato->>'Material','?') from fugates.integracao i join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id left join fugates.contrato_sap_toledo_versao w on w.integracao_id=i.id and w.versao=1 where p.erro_codigo like 'R170_%MAPPING_REQUIRED' order by 1" | sed 's/^/   /'
  exit 0
fi

# "corrigir" troca o codigo Guardian de um mapeamento que ja existe. Existe porque acrescentar uma
# segunda entrada para o mesmo identificador seria ambiguidade, e o motor passaria a bloquear.
CORRIGIR=0
if [ "$TIPO" = corrigir ]; then CORRIGIR=1; TIPO=${2:-}; SAP=${3:-}; GUARDIAN=${4:-}; fi

case "$TIPO" in
  parceiro) CHAVE=SapPartnerId; GRUPO=ApprovedPartnerMappings; MAXCOD=20 ;;
  material) CHAVE=SapMaterial;  GRUPO=ApprovedMaterialMappings; MAXCOD=30 ;;
  *) echo "uso: sudo bash $0 parceiro|material <identificador-sap> <codigo-guardian>"
     echo "     sudo bash $0 corrigir parceiro|material <identificador-sap> <novo-codigo>"
     echo "     sudo bash $0 listar"; exit 1 ;;
esac

# Entrada tem de ser digitos: identificador com espaco ou letra nunca casaria com o snapshot, e o
# erro so apareceria como bloqueio silencioso horas depois.
echo "$SAP" | grep -qE '^[0-9]+$' || { echo "BLOCKED: identificador SAP invalido: '$SAP'"; exit 1; }
echo "$GUARDIAN" | grep -qE '^[0-9]+$' || { echo "BLOCKED: codigo Guardian invalido: '$GUARDIAN'"; exit 1; }
test "${#GUARDIAN}" -le "$MAXCOD" || { echo "BLOCKED: codigo Guardian excede $MAXCOD caracteres"; exit 1; }

# Duplicata e ambiguidade, e o motor bloqueia por isso. Melhor barrar aqui, com mensagem clara.
EXISTENTE=$(grep -E "^AcmR57__Normal__${GRUPO}__[0-9]+__${CHAVE}=${SAP}$" "$ENVR57" | head -1 || true)
if [ "$CORRIGIR" = 0 ] && [ -n "$EXISTENTE" ]; then
  echo "BLOCKED: $TIPO $SAP ja esta mapeado. Mapeamento duplicado torna a resolucao ambigua"
  echo "         e o motor passaria a bloquear este identificador."
  echo "         Para trocar o codigo:  sudo bash $0 corrigir $TIPO $SAP <novo-codigo>"
  mostrar
  exit 1
fi
if [ "$CORRIGIR" = 1 ] && [ -z "$EXISTENTE" ]; then
  echo "BLOCKED: nao ha mapeamento de $TIPO $SAP para corrigir. Para criar:"
  echo "         sudo bash $0 $TIPO $SAP $GUARDIAN"
  exit 1
fi

# Confirma que o identificador existe de fato em algum contrato recebido - evita mapear um valor
# digitado errado, que ficaria inerte sem ninguem perceber.
COLUNA=$([ "$TIPO" = parceiro ] && echo IdParceiro || echo Material)
USOS=$(psql_acm "select count(*) from fugates.contrato_sap_toledo_versao where payload_contrato->>'$COLUNA'='$SAP'")
if [ "${USOS:-0}" = 0 ]; then
  echo "BLOCKED: nenhum contrato recebido usa o $TIPO '$SAP'."
  echo "         Confira a forma gravada no banco (com zeros a esquerda) antes de mapear."
  exit 1
fi
echo "-- $TIPO $SAP aparece em $USOS contrato(s) recebido(s)  OK"

cp "$ENVR57" "$ENVR57.bak.$SELO"
if [ "$CORRIGIR" = 1 ]; then
  IDX=$(indice_de "$EXISTENTE")
  ANTES=$(grep -E "^AcmR57__Normal__${GRUPO}__${IDX}__GuardianCode=" "$ENVR57" | cut -d= -f2-)
  test -n "$ANTES" || { echo "BLOCKED: indice $IDX sem GuardianCode; arquivo inconsistente"; exit 1; }
  sed -i "s|^AcmR57__Normal__${GRUPO}__${IDX}__GuardianCode=.*$|AcmR57__Normal__${GRUPO}__${IDX}__GuardianCode=${GUARDIAN}|" "$ENVR57"
  sed -i "s|^AcmR57__Normal__${GRUPO}__${IDX}__EvidenceReference=.*$|AcmR57__Normal__${GRUPO}__${IDX}__EvidenceReference=R170-CORRECAO-OPERADOR-${SELO}|" "$ENVR57"
  echo "-- indice $IDX corrigido: $TIPO $SAP  $ANTES -> $GUARDIAN  (backup: $ENVR57.bak.$SELO)"
  echo "   ATENCAO: contratos ja cadastrados com $ANTES NAO sao corrigidos por isto. O motor nao"
  echo "   reenvia contrato cuja etapa ja esta confirmada; correcao de cadastro existente exige"
  echo "   AlteraContrato no Guardian, decidido a parte."
else
  IDX=$(grep -oE "^AcmR57__Normal__${GRUPO}__[0-9]+__Centro=" "$ENVR57" \
          | sed -E "s/^AcmR57__Normal__${GRUPO}__([0-9]+)__Centro=/\1/" | sort -n | tail -1 || true)
  IDX=$(( ${IDX:--1} + 1 ))
  cat >> "$ENVR57" <<EOF
# $TIPO $SAP -> Guardian $GUARDIAN, aprovado pelo operador em $SELO.
AcmR57__Normal__${GRUPO}__${IDX}__Centro=3011
AcmR57__Normal__${GRUPO}__${IDX}__${CHAVE}=${SAP}
AcmR57__Normal__${GRUPO}__${IDX}__GuardianCode=${GUARDIAN}
AcmR57__Normal__${GRUPO}__${IDX}__EvidenceReference=R170-MAPEAMENTO-OPERADOR-${SELO}
EOF
  echo "-- gravado no indice $IDX (backup: $ENVR57.bak.$SELO)"
fi
chown root:michael "$ENVR57"; chmod 640 "$ENVR57"

systemctl restart "$SERVICO"
sleep 5
if ! systemctl is-active --quiet "$SERVICO"; then
  echo "   BLOCKED: o motor nao subiu apos a mudanca. Restaurando o arquivo anterior."
  cp "$ENVR57.bak.$SELO" "$ENVR57"
  systemctl restart "$SERVICO" || true
  journalctl -u "$SERVICO" --since "-2 min" --no-pager -q 2>/dev/null \
    | grep -iE "exception|fail|erro|exige|invalid|required" | tail -6 | sed 's/^/     /'
  exit 1
fi
echo "-- motor reiniciado e ativo"
reavaliar
echo "-- aguardando o motor processar a fila (30s)..."
sleep 30
echo
mostrar
echo
echo "-- situacao dos contratos:"
psql_acm "select i.chave_negocio||'  '||i.estado||'  '||coalesce(p.etapa_funcional,'(sem etapa)')||'  '||coalesce(p.erro_codigo,'-') from fugates.integracao i left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id where i.operacao='SAP_CONTRATO_TOLEDO' order by 1" | sed 's/^/   /'
