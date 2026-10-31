#!/usr/bin/env bash
# R170 - Explica, para cada contrato recebido do SAP, por que ele foi ou nao foi cadastrado.
#
# Existe porque uma das recusas nao deixa rastro: quando a combinacao de negocio nao e aprovada, o
# motor pula o contrato ANTES do claim - de proposito, para nao ter efeito colateral - e por isso
# nao grava tentativa nem etapa. Visto de fora, o contrato parece simplesmente travado.
#
# Leitura apenas. Nao altera nada, nao reinicia nada, nao fala com o Guardian.
#
# Uso:  sudo bash porque-nao-cadastrou-r170.sh            (todos)
#       sudo bash porque-nao-cadastrou-r170.sh 0000001165 (um contrato)
set -euo pipefail

ALVO=${1:-}
ENVR57=/etc/fugates/motor-dev/r57.env
PGPASS=/home/michael/.fugates-acm-r165p/pgpass

# Mesma lista de AcmInboundWindowOptions.HistoricalContracts. Estes contratos foram exercitados em
# etapas anteriores e estao permanentemente fora do cadastro automatico.
HISTORICOS="1031 1106 1138 1139 1140 1144 1147 1155"

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo (preciso ler $ENVR57)"; exit 1; }
test -f "$ENVR57" || { echo "BLOCKED: $ENVR57 nao existe; o motor continuo nao esta instalado"; exit 1; }

psql_acm() { sudo -u michael env PGPASSFILE=$PGPASS psql -w \
  'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc "$1" || true; }

mapeado() { grep -qE "^AcmR57__Normal__Approved(Partner|Material)Mappings__[0-9]+__$1=$2$" "$ENVR57"; }
historico() {
  local n="${1#"${1%%[1-9]*}"}"
  for h in $HISTORICOS; do [ "$h" = "$n" ] && return 0; done
  return 1
}

FILTRO=""
[ -n "$ALVO" ] && FILTRO="and i.chave_negocio = '$ALVO'"

echo "=== Contratos recebidos do SAP e por que cada um esta onde esta ==="
echo

LINHAS=$(psql_acm "select i.chave_negocio
  ||'|'||coalesce(w.payload_contrato->>'TipoContrato','?')
  ||'|'||coalesce(w.payload_contrato->>'TipoPesagem','?')
  ||'|'||coalesce(w.payload_contrato->>'OperacaoContrato','?')
  ||'|'||coalesce(w.payload_contrato->>'Centro','?')
  ||'|'||coalesce(w.payload_contrato->>'StatusContrato','?')
  ||'|'||coalesce(w.payload_contrato->>'IdParceiro','?')
  ||'|'||coalesce(w.payload_contrato->>'Material','?')
  ||'|'||coalesce(w.payload_contrato->>'Item','(sem item)')
  ||'|'||i.estado
  ||'|'||coalesce(p.etapa_funcional,'-')
  ||'|'||coalesce(p.erro_codigo,'-')
  ||'|'||coalesce(split_part(p.resultado_funcional,':',4),'-')
  ||'|'||coalesce(i.proxima_tentativa_em::text,'(sem agendamento)')
  ||'|'||case when i.proxima_tentativa_em is not null and i.proxima_tentativa_em <= clock_timestamp() then 'agora' else 'depois' end
  from fugates.integracao i
  join fugates.contrato_sap_toledo_versao w on w.integracao_id=i.id and w.versao=1
  left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id
  where i.operacao='SAP_CONTRATO_TOLEDO' $FILTRO
  order by i.chave_negocio")

test -n "$LINHAS" || { echo "Nenhum contrato encontrado${ALVO:+ para $ALVO}."; exit 0; }

ESPERANDO_MAPA=""
ESPERANDO_AMANHA=""
while IFS='|' read -r NUM TIPO PES OP CENTRO ST PARCEIRO MATERIAL ITEM ESTADO ETAPA ERRO SEQ PROXIMA QUANDO; do
  [ -z "$NUM" ] && continue
  printf '%s  %s/%s/%s  parceiro=%s  material=%s\n' "$NUM" "$TIPO" "$PES" "$OP" "$PARCEIRO" "$MATERIAL"

  # 1) Dado sintetico de teste nunca foi contrato real do SAP.
  if ! echo "$PARCEIRO$MATERIAL" | grep -qE '^[0-9]+$'; then
    echo "   DADO SINTETICO de teste, nao veio do SAP. Ignore nesta analise."
    echo; continue
  fi

  # 2) Historico: fora do cadastro automatico por desenho, independentemente de mapeamento.
  if historico "$NUM"; then
    echo "   FORA DO AUTOMATICO por desenho: esta na lista de contratos historicos, ja exercitados"
    echo "   em etapas anteriores. Estado atual: $ESTADO${ERRO:+ / $ERRO}."
    echo "   Mapeamento de identificador NAO muda isto - ele nunca sera reprocessado."
    echo; continue
  fi

  # 3) Ja cadastrado? A etapa confirmada e a prova; o motor nao reenvia contrato nesse estado.
  if [ "$ETAPA" = "R165U_PROTOCOL_BLOCKED" ] || [ "$ETAPA" = "R165U_CONFIRMED" ]; then
    echo "   CADASTRADO no Guardian${SEQ:+, sequencial $SEQ}."
    [ "$ERRO" = "R165U_CALLBACK_APPROVAL_REQUIRED" ] && \
      echo "   Parou na etapa REST (pre-cadastro), que esta desligada por configuracao. Nao e falha."
    echo; continue
  fi

  # 4) Estado terminal: nao adianta mexer em mapeamento.
  case "$ESTADO" in
    FALHA_DEFINITIVA)
      echo "   FALHA DEFINITIVA, nao sera retentado: $ERRO (etapa $ETAPA)."
      echo; continue ;;
    RESULTADO_INDETERMINADO)
      echo "   RESULTADO INDETERMINADO em $ETAPA: $ERRO."
      echo "   Exige reconciliacao manual; o motor nao decide sozinho um desfecho desconhecido."
      echo; continue ;;
  esac

  # 5) Combinacao de negocio. Esta e a recusa que NAO deixa rastro no banco.
  PAR="$TIPO/$PES/$OP"
  if [ "$PAR" != "AC3P/E/10" ] && [ "$PAR" != "AC3S/S/40" ]; then
    echo "   BLOQUEADO: combinacao de negocio nao aprovada ($PAR)."
    echo "   Aprovadas hoje: compra AC3P/E/10 e venda AC3S/S/40, como pares fechados."
    echo "   O motor recusa ANTES do claim, por isso nao ha tentativa nem etapa registrada."
    echo "   Para liberar: definir o TipoLancamento do Guardian para esta operacao e aprova-la."
    echo; continue
  fi
  if [ "$CENTRO" != "3011" ] || [ "$ST" != "T" ] || [ "$ITEM" != "(sem item)" ]; then
    echo "   BLOQUEADO: centro=$CENTRO status=$ST item=$ITEM. Aprovado: centro 3011, status T, sem item."
    echo; continue
  fi

  # 6) Identificadores. Fail-closed: sem codigo Guardian aprovado, nada e enviado.
  FALTA=""
  mapeado SapPartnerId "$PARCEIRO" || FALTA="parceiro $PARCEIRO"
  mapeado SapMaterial "$MATERIAL" || FALTA="${FALTA:+$FALTA e }material $MATERIAL"
  if [ -n "$FALTA" ]; then
    echo "   BLOQUEADO: sem codigo Guardian aprovado para $FALTA."
    case "$FALTA" in *parceiro*) echo "     sudo bash /home/michael/mapear-identificador-r170.sh parceiro $PARCEIRO <codigo>" ;; esac
    case "$FALTA" in *material*) echo "     sudo bash /home/michael/mapear-identificador-r170.sh material $MATERIAL <codigo>" ;; esac
    ESPERANDO_MAPA="sim"
    echo; continue
  fi

  # 7) Tudo aprovado: so resta quando o motor vai olhar de novo. O backoff e de 1 dia, entao um
  #    contrato que ja falhou nao e reavaliado hoje so porque o mapeamento mudou.
  if [ "$QUANDO" = "agora" ]; then
    echo "   ELEGIVEL, aguardando o proximo ciclo do motor (estado=$ESTADO)."
  else
    echo "   APROVADO, mas so sera retentado em $PROXIMA (backoff de 1 dia apos a falha anterior)."
    echo "   Para reavaliar agora:  sudo bash /home/michael/mapear-identificador-r170.sh reavaliar"
    ESPERANDO_AMANHA="sim"
  fi
  echo
done <<EOF
$LINHAS
EOF

echo "--- motor: $(systemctl is-active fugates-acm-motor-dev) / $(systemctl is-enabled fugates-acm-motor-dev 2>/dev/null || echo '?')"
[ -n "$ESPERANDO_MAPA" ] && echo "--- ha contratos esperando mapeamento de identificador (ver acima)."
[ -n "$ESPERANDO_AMANHA" ] && echo "--- ha contratos aprovados apenas aguardando o backoff; use 'reavaliar'."
exit 0
