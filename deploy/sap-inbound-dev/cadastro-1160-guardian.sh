#!/usr/bin/env bash
# R169 - Cadastro real UNICO do contrato 0000001160 no Guardian HML, pelo motor FugaTES.
#
# AUTORIZACAO: operador, gate R169. Escopo: somente o contrato 0000001160, operacao SOAP
# CadastraContrato, ambiente HML, no maximo UM POST real.
#
# O que este script faz:
#   1. Reconcilia: confirma no Guardian que 1160 NAO existe, e no banco que o contrato esta la.
#   2. Monta a janela R57 com o mapeamento SAP->Guardian e as quatro referencias de evidencia.
#   3. Liga o motor com escopo piloto no 1160 e espera o despacho.
#   4. DESABILITA a execucao imediatamente apos a tentativa, com ou sem sucesso.
#   5. Mostra o resultado empresarial (Erro/Sequencial) e o estado final no banco.
#
# Nao repete POST. Se houver timeout ou resposta ambigua, para e devolve indeterminado.
#
# USO:
#   sudo bash cadastro-1160-guardian.sh conferir   # so reconcilia, nao liga nada
#   sudo bash cadastro-1160-guardian.sh executar

set -euo pipefail

CONTRATO=0000001160
# A consulta de duplicidade usa as duas formas do codigo; ver a reconciliacao.
ENVDIR=/etc/fugates/motor-dev
ENVBASE=$ENVDIR/motor.env
ENVESCOPO=$ENVDIR/escopo.env
ENVR57=$ENVDIR/r57.env
SERVICO=fugates-acm-motor-dev
PGPASS=/home/michael/.fugates-acm-r165p/pgpass
SOAP=http://192.168.17.91:80/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx
ACAO="${1:-conferir}"

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }
psql_acm() { sudo -u michael env PGPASSFILE=$PGPASS psql -w \
  'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc "$1" || true; }

echo "=== R169 - cadastro do contrato $CONTRATO no Guardian HML ==="
echo

# ---------------------------------------------------------------- 1) reconciliacao
echo "-- reconciliacao:"
# O codigo enviado ao Guardian e contrato.NumeroContrato, COM os zeros a esquerda - a forma gravada
# no banco. Consultar apenas "1160" nao protege nada: um cadastro feito como "0000001160" responde
# 309851 na forma curta. Por isso as duas formas sao consultadas e ambas precisam estar ausentes.
for CODIGO_GUARDIAN in "$CONTRATO" "${CONTRATO#"${CONTRATO%%[1-9]*}"}"; do
cat > /tmp/r169_q.xml <<XML
<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:ws="http://toledobrasil.com.br/WS_Guardian">
<soapenv:Header/><soapenv:Body><ws:ConsultaContrato><ws:voConfiguracaoContrato/>
<ws:voDadosContratoCodigo><ws:CodigoContrato>$CODIGO_GUARDIAN</ws:CodigoContrato></ws:voDadosContratoCodigo>
</ws:ConsultaContrato></soapenv:Body></soapenv:Envelope>
XML
HTTP=$(curl -s -m 25 -o /tmp/r169_r.xml -w "%{http_code}" -X POST "$SOAP" \
  -H "Content-Type: text/xml; charset=utf-8" \
  -H "SOAPAction: \"http://toledobrasil.com.br/WS_Guardian/ConsultaContrato\"" \
  --data-binary @/tmp/r169_q.xml || echo 000)
ERRO=$(grep -oE "<Erro>[^<]*</Erro>" /tmp/r169_r.xml 2>/dev/null | head -1 | sed 's/<[^>]*>//g')
rm -f /tmp/r169_q.xml
if [ "$HTTP" != 200 ]; then
  echo "   BLOCKED: consulta ao Guardian falhou (HTTP=$HTTP) para $CODIGO_GUARDIAN. Nao executar POST."
  rm -f /tmp/r169_r.xml; exit 1
fi
if [ "$ERRO" != 309851 ]; then
  echo "   BLOCKED: o Guardian NAO confirmou ausencia do contrato $CODIGO_GUARDIAN (Erro=$ERRO)."
  grep -oE "<Descricao>[^<]*</Descricao>|<Sequencial>[^<]*</Sequencial>" /tmp/r169_r.xml | head -3 | sed 's/^/     /'
  echo "   Cadastro preexistente ou indeterminado: nao executar POST. Reconciliar com a Hera."
  rm -f /tmp/r169_r.xml; exit 1
fi
rm -f /tmp/r169_r.xml
echo "   Guardian: contrato $CODIGO_GUARDIAN ausente (Erro=309851)  OK"
done

EXISTE=$(psql_acm "select count(*) from fugates.contrato_sap_toledo_versao where numero_contrato='$CONTRATO' and versao=1")
test "${EXISTE:-0}" = 1 || { echo "   BLOCKED: contrato $CONTRATO versao 1 nao esta no banco."; exit 1; }
# concat_ws + coalesce: '||' tem a mesma precedencia de '->>' e associa a esquerda, o que fazia o
# Postgres tentar concatenar jsonb com texto e abortar a consulta. O '?' por campo garante que um
# valor ausente apareca na mensagem em vez de virar string vazia.
ESCOPO=$(psql_acm "select concat_ws('/', coalesce(payload_contrato->>'TipoContrato','?'), coalesce(payload_contrato->>'TipoPesagem','?'), coalesce(payload_contrato->>'OperacaoContrato','?'), coalesce(payload_contrato->>'Centro','?'), coalesce(payload_contrato->>'StatusContrato','?')) from fugates.contrato_sap_toledo_versao where numero_contrato='$CONTRATO'")
test "$ESCOPO" = "AC3S/S/40/3011/T" || { echo "   BLOCKED: escopo inesperado: $ESCOPO"; exit 1; }

# A janela nao tem lista de contratos PERMITIDOS, apenas de excluidos. Qualquer outro contrato que
# ela admitiria seria despachado junto, o que violaria a autorizacao de um unico cadastro. Por isso
# o conjunto admissivel e levantado agora e tudo que nao for este contrato entra como exclusao.
ADMISSIVEIS=$(psql_acm "select i.chave_negocio from fugates.integracao i join fugates.contrato_sap_toledo_versao w on w.integracao_id=i.id and w.versao=1 where i.origem='SAP' and i.destino='TOLEDO' and i.operacao='SAP_CONTRATO_TOLEDO' and w.payload_contrato->>'Centro'='3011' and (w.payload_contrato->>'Item') is null and w.payload_contrato->>'StatusContrato'='T' and w.payload_contrato->>'IdParceiro'='0020000051' and w.payload_contrato->>'Material'='000000000007000002' and ((w.payload_contrato->>'TipoContrato'='AC3P' and w.payload_contrato->>'TipoPesagem'='E' and w.payload_contrato->>'OperacaoContrato'='10') or (w.payload_contrato->>'TipoContrato'='AC3S' and w.payload_contrato->>'TipoPesagem'='S' and w.payload_contrato->>'OperacaoContrato'='40')) and ltrim(i.chave_negocio,'0') not in ('1031','1106','1138','1139','1140','1144','1147','1155') order by 1")
echo "$ADMISSIVEIS" | grep -qx "$CONTRATO" || { echo "   BLOCKED: a janela nao admitiria $CONTRATO; admissiveis: $(echo $ADMISSIVEIS)"; exit 1; }
OUTROS=$(echo "$ADMISSIVEIS" | sed '/^$/d' | grep -vx "$CONTRATO" || true)
if [ -n "$OUTROS" ]; then
  echo "   Outros contratos com o mesmo perfil de negocio, NAO afetados por esta operacao:"
  echo "$OUTROS" | sed 's/^/     - /'
  echo "     (o escopo e por numero de contrato: MotorIntegracao.DentroDoEscopoAsync compara"
  echo "      ChaveNegocio com AcmPilotNumeroContrato antes de qualquer claim)"
else
  echo "   Nenhum outro contrato com o mesmo perfil  OK"
fi
echo "   Banco: contrato presente, escopo $ESCOPO  OK"

INDET=$(psql_acm "select count(*) from fugates.tentativa_integracao t join fugates.integracao i on i.id=t.integracao_id where i.chave_negocio='$CONTRATO' and t.envio_iniciado_em is not null")
test "${INDET:-0}" = 0 || { echo "   BLOCKED: existe tentativa com envio efetivamente iniciado. Reconciliar antes."; exit 1; }
TENT=$(psql_acm "select count(*) from fugates.tentativa_integracao t join fugates.integracao i on i.id=t.integracao_id where i.chave_negocio='$CONTRATO'")
echo "   Tentativas anteriores: ${TENT:-0}, todas sem envio iniciado  OK"

if [ "$ACAO" = conferir ]; then
  echo; echo "Modo conferir: reconciliacao PASS. Nada foi alterado nem enviado."; exit 0
fi

# ---------------------------------------------------------------- 2) governanca e escopo
echo
echo "-- escopo: piloto por contrato $CONTRATO, centro 3011, sem item (janela R57 desligada)"

cat > "$ENVR57" <<EOF
# R169 - governanca e escopo do cadastro unico do contrato $CONTRATO.
# Referencias apontam para docs/Coordination/EVIDENCIAS_R57_CONTRATO_1160.md, que registra as
# verificacoes efetivamente executadas em 29/10/2026.
AcmR57__Normal__AuthorizationReference=R169-OPERADOR-1160
AcmR57__Normal__GuardianIdentityEvidence=R169-GUARDIAN-IDENTITY-20261029
AcmR57__Normal__PostgreSqlIdentityEvidence=R169-POSTGRESQL-IDENTITY-20261029
AcmR57__Normal__JournalEvidence=R169-JOURNAL-20261029
AcmR57__Normal__IdentifierMappingEvidence=R169-MAPEAMENTO-20261029
AcmR57__Normal__ProximityValueEvidence=R169-PROXIMIDADE-20261029
AcmR57__Normal__ValorProximidadeFimContrato=0
# Modo de identificadores: mapeamento por janela. As outras duas aprovacoes verbatim ficam FALSE,
# exigencia de ResolverWindowMappings.
AcmR57__Normal__SapContractCopiedVerbatimApproved=true
AcmR57__Normal__SapIdentifiersCopiedVerbatimApproved=false
AcmR57__Normal__SapContractAndMaterialCopiedVerbatimApproved=false
# A janela de admissao R57 fica DESLIGADA de proposito. Ela exige correlacao prefixada com
# "r165ag:cpi:<janela>:" e criado_em DENTRO da janela - ou seja, serve para contratos que chegam
# com ela aberta. Este contrato chegou as 10:53 pelo receptor SAP, com correlacao sem prefixo:
# nenhuma janela aberta agora poderia admiti-lo. Para contrato preexistente o mecanismo e o piloto,
# que recorta por numero de contrato, centro e item. Os dois sao mutuamente exclusivos por
# MotorIntegracaoOptions.EhValida().
MotorIntegracao__AcmInboundWindow__Enabled=false
# Mapeamento SAP -> Guardian por contrato, nas formas GRAVADAS no banco. Sem janela, quem resolve
# emissor e item e ResolverCodigoEmissor/ResolverCodigoItem pelo par ApprovedPartnerMapping +
# ApprovedMaterialMapping; os dois precisam existir junto com SapContractCopiedVerbatimApproved.
# Item e omitido nos dois: o transporte SAP nao entrega a chave e o snapshot a tem nula.
AcmR57__Normal__ApprovedPartnerMapping__NumeroContrato=$CONTRATO
AcmR57__Normal__ApprovedPartnerMapping__Centro=3011
AcmR57__Normal__ApprovedPartnerMapping__SapPartnerId=0020000051
AcmR57__Normal__ApprovedPartnerMapping__GuardianCode=20000051
AcmR57__Normal__ApprovedPartnerMapping__EvidenceReference=R169-MAPEAMENTO-20261029
AcmR57__Normal__ApprovedMaterialMapping__NumeroContrato=$CONTRATO
AcmR57__Normal__ApprovedMaterialMapping__Centro=3011
AcmR57__Normal__ApprovedMaterialMapping__SapMaterial=000000000007000002
AcmR57__Normal__ApprovedMaterialMapping__GuardianCode=7000000
AcmR57__Normal__ApprovedMaterialMapping__EvidenceReference=R169-MAPEAMENTO-20261029
EOF
chown root:michael "$ENVR57"; chmod 640 "$ENVR57"

# A unidade precisa carregar este arquivo. Acrescenta o EnvironmentFile se ainda nao houver.
UNIT=/etc/systemd/system/$SERVICO.service
grep -q "EnvironmentFile=$ENVR57" "$UNIT" || \
  sed -i "s|^EnvironmentFile=$ENVBASE|EnvironmentFile=$ENVBASE\nEnvironmentFile=$ENVR57|" "$UNIT"
grep -q "EnvironmentFile=$ENVR57" "$UNIT" || { echo "BLOCKED: nao foi possivel adicionar o EnvironmentFile da janela"; exit 1; }

# ---------------------------------------------------------------- 3) execucao
# O piloto por contrato NAO pode coexistir com a janela: MotorIntegracaoOptions.EhValida() exige
# (!UsaJanelaAcm || (!UsaPilotoAcm && AcmInboundWindow.EhValida())), e o motor aborta na partida se
# os dois estiverem definidos. O recorte a um unico contrato vem das exclusoes acima.
# Escopo da execucao: o piloto nomeia o contrato. DentroDoEscopoAsync exige origem SAP, destino
# TOLEDO, operacao SAP_CONTRATO_TOLEDO, ChaveNegocio igual a este numero, Centro igual e Item nulo,
# e essa checagem ocorre ANTES do claim - contratos fora do escopo nao sao tocados. AcmPilotItem
# fica ausente de proposito. Isto tambem satisfaz o ExecStartPre da unit.
printf 'MotorIntegracao__AcmPilotNumeroContrato=%s\nMotorIntegracao__AcmPilotCentro=3011\n' "$CONTRATO" > "$ENVESCOPO"
chown root:michael "$ENVESCOPO"; chmod 640 "$ENVESCOPO"

ANTES=$(psql_acm "select count(*) from fugates.tentativa_integracao t join fugates.integracao i on i.id=t.integracao_id where i.chave_negocio='$CONTRATO'")
psql_acm "update fugates.integracao set proxima_tentativa_em = clock_timestamp() where chave_negocio='$CONTRATO'" >/dev/null

systemctl daemon-reload
systemctl start "$SERVICO"
echo "-- motor ligado; aguardando o despacho (ate 60s)"
# Falha de partida precisa ser vista: sem isto o script espera 60s e reporta como se nada tivesse
# acontecido, que foi o que aconteceu na tentativa de 13:39.
PARTIDA_OK=1
sleep 3
if ! systemctl is-active --quiet "$SERVICO"; then
  PARTIDA_OK=0
  echo "   BLOCKED: o motor nao subiu. Ultimas linhas do log:"
  journalctl -u "$SERVICO" --since "-2 min" --no-pager -q 2>/dev/null \
    | grep -iE "exception|fail|erro|exige|invalid" | tail -8 | sed 's/^/     /'
fi
for _ in $(seq 1 20); do
  [ "$PARTIDA_OK" = 1 ] || break
  sleep 3
  DEPOIS=$(psql_acm "select count(*) from fugates.tentativa_integracao t join fugates.integracao i on i.id=t.integracao_id where i.chave_negocio='$CONTRATO'")
  [ "${DEPOIS:-0}" -gt "${ANTES:-0}" ] && break
done

# ---------------------------------------------------------------- 4) desabilitar SEMPRE
systemctl stop "$SERVICO" 2>/dev/null || true
printf '# desligado apos a tentativa unica R169\n' > "$ENVESCOPO"
printf '# janela encerrada apos a tentativa unica R169\n' > "$ENVR57"
chown root:michael "$ENVESCOPO" "$ENVR57"; chmod 640 "$ENVESCOPO" "$ENVR57"
systemctl daemon-reload
echo "-- motor parado, janela fechada e escopo limpo"
test "$PARTIDA_OK" = 1 || { echo; echo "BLOCKED: o motor nao iniciou. Nenhuma tentativa foi criada e nada foi enviado."; exit 1; }

# ---------------------------------------------------------------- 5) resultado
echo
echo "=== RESULTADO ==="
psql_acm "select 'tentativa: '||t.desfecho||' fase='||t.fase_envio||' envio_iniciado_em='||coalesce(t.envio_iniciado_em::text,'(nulo)')||' em '||t.criada_em::text from fugates.tentativa_integracao t join fugates.integracao i on i.id=t.integracao_id where i.chave_negocio='$CONTRATO' order by t.criada_em desc limit 1" | sed 's/^/  /'
psql_acm "select 'integracao: estado='||estado||' fase='||fase_envio from fugates.integracao where chave_negocio='$CONTRATO'" | sed 's/^/  /'
psql_acm "select 'auditoria: '||string_agg(evento||coalesce(' ['||nullif(diagnostico,'')||']',''),' -> ' order by criada_em) from fugates.auditoria_integracao a join fugates.integracao i on i.id=a.integracao_id where i.chave_negocio='$CONTRATO' and a.criada_em > now()-interval '5 minutes'" | sed 's/^/  /'
echo
echo "-- Guardian, apos a tentativa:"
cat > /tmp/r169_q2.xml <<XML
<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/" xmlns:ws="http://toledobrasil.com.br/WS_Guardian">
<soapenv:Header/><soapenv:Body><ws:ConsultaContrato><ws:voConfiguracaoContrato/>
<ws:voDadosContratoCodigo><ws:CodigoContrato>$CONTRATO</ws:CodigoContrato></ws:voDadosContratoCodigo>
</ws:ConsultaContrato></soapenv:Body></soapenv:Envelope>
XML
curl -s -m 25 -o /tmp/r169_r2.xml -X POST "$SOAP" -H "Content-Type: text/xml; charset=utf-8" \
  -H "SOAPAction: \"http://toledobrasil.com.br/WS_Guardian/ConsultaContrato\"" --data-binary @/tmp/r169_q2.xml || true
grep -oE "<Erro>[^<]*</Erro>|<Descricao>[^<]*</Descricao>|<Sequencial>[^<]*</Sequencial>|<Codigo>[^<]*</Codigo>" /tmp/r169_r2.xml 2>/dev/null | head -5 | sed 's/^/  /'
rm -f /tmp/r169_q2.xml /tmp/r169_r2.xml
echo
echo "Execucao encerrada. Nenhum novo POST sera feito: janela fechada e escopo limpo."
