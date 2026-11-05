#!/usr/bin/env bash
# Libera a criacao do PROTOCOLO logo depois da criacao do CONTRATO, no motor ACM.
#
# NAO HA MUDANCA DE CODIGO AQUI. O fluxo que o operador descreve - cria o contrato por SOAP e, em
# seguida, o protocolo por REST - ja esta construido em AcmR57Pipeline.Normal e nasce de proposito
# sem transportadora, sem documento e sem dados de pesagem (CriacaoInicialSemDadosPesagem). O que
# recusava era um portao de CONFIGURACAO: AcmR57NormalPreparation.PrepararProtocolo exige callback
# aprovado, e no ambiente estava AcmR57__Normal__CallbackApproved=false. Nada mais.
#
# O que a politica exige (AcmR104CallbackPolicy.Validar). Como o Guardian devolve por HTTP, vale
# somente a excecao documentada: UMA url, e um binding exato.
#
# DOIS DOS VALORES SAO ATESTADOS, nao configuracao: SourceAllowlistVerified afirma que a origem do
# Guardian esta liberada, e PreCadastroReturnRouteBindingVerified afirma que a rota de retorno foi
# conferida. Este script NAO escreve "true" de cabeca: ele reconfere os dois fatos no momento da
# escrita e se recusa a seguir se qualquer um nao se sustentar. Atestado sem lastro e pior do que
# bloqueio, porque o bloqueio e visivel e o atestado falso nao.
#
#   origem  -> regra de aceite para 192.168.17.91 na porta 80, na cadeia FUGATES_HTTP80
#   rota    -> location exact match no nginx, com a origem liberada e proxy para o receptor
#   receptor-> o servico atende a rota em 127.0.0.1:18086
#
# Uso:  sudo bash habilitar-protocolo-acm.sh conferir
#       sudo bash habilitar-protocolo-acm.sh instalar --evidencia <REFERENCIA>
#       sudo bash habilitar-protocolo-acm.sh reverter
#
# --evidencia e obrigatorio e nao tem valor padrao de proposito: a referencia tem de nomear a
# conferencia que FOI feita, por quem e quando. Um padrao generico seria um atestado fabricado.
set -euo pipefail

MODO=${1:-}
shift || true
EVIDENCIA=
while [ $# -gt 0 ]; do
  case "$1" in
    --evidencia) EVIDENCIA=${2:-}; shift 2 ;;
    *) echo "BLOCKED: argumento desconhecido: $1"; exit 1 ;;
  esac
done

SERVICO=fugates-acm-motor-dev
ENVDIR=/etc/fugates/motor-dev
ENVPROTO=$ENVDIR/protocolo-callback.env
DROPDIR=/etc/systemd/system/$SERVICO.service.d
URL=http://192.168.17.40/api/guardian/set26/callback/recebimento
ORIGEM_GUARDIAN=192.168.17.91
CIDR=192.168.17.91/32
RECEPTOR=127.0.0.1:18086
ROTA=/api/guardian/set26/callback/recebimento
SELO=$(date -u +%Y%m%d_%H%M%SZ)

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }
case "$MODO" in conferir|instalar|reverter) ;; *) echo "uso: sudo bash $0 conferir|instalar --evidencia <REF>|reverter"; exit 1 ;; esac

efetivo() { systemctl show -p "$1" --value "$SERVICO" 2>/dev/null; }

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
  ULTIMO=$(ls -1t "$BINDING".bak.protocolo.* 2>/dev/null | head -1 || true)
  test -n "$ULTIMO" || { echo "BLOCKED: nenhum backup .bak.protocolo.* de $BINDING"; exit 1; }
  cp "$ULTIMO" "$BINDING"
  rm -f "$ENVPROTO"
  systemctl daemon-reload
  systemctl restart "$SERVICO"
  sleep 6
  echo "-- binding restaurado de $ULTIMO; $ENVPROTO removido"
  systemctl is-active --quiet "$SERVICO" && echo "-- motor ATIVO; o protocolo volta a ser bloqueado por callback" \
    || { echo "   BLOCKED: motor nao subiu apos reverter"; journalctl -u "$SERVICO" --since "-2 min" --no-pager -q | tail -8 | sed 's/^/     /'; exit 1; }
  exit 0
fi

# ---------------------------------------------------------------- estado
echo "=== configuracao de callback hoje ==="
grep -hE '^AcmR57__Normal__(Callback|IdAgenda)' "$ENVDIR"/*.env 2>/dev/null | sort | sed 's/^/   /' \
  || echo "   (nenhuma chave de callback declarada)"
echo
echo "=== o que a politica exige ==="
echo "   url ................ $URL"
echo "   Environment ........ HML"
echo "   SourceCidr ......... $CIDR"
echo "   os dois Verified ... atestados, conferidos abaixo"

FALHAS=0
nota() { printf '   %-52s %s\n' "$1" "$2"; }

echo
echo "=== conferencia dos atestados ==="

# 1) origem liberada no firewall, com contador de trafego real como reforco.
REGRA=$(nft list ruleset 2>/dev/null | grep -E "saddr $ORIGEM_GUARDIAN\b.*dport 80.*accept" | head -1 || true)
if [ -n "$REGRA" ]; then
  PACOTES=$(printf '%s' "$REGRA" | sed -nE 's/.*packets ([0-9]+).*/\1/p')
  nota "firewall aceita $ORIGEM_GUARDIAN na porta 80" "OK (packets=${PACOTES:-?})"
  [ "${PACOTES:-0}" -gt 0 ] 2>/dev/null \
    && nota "  e ja houve trafego real dessa origem" "OK" \
    || nota "  AVISO: contador zerado; regra existe mas nunca foi usada" "-"
else
  nota "firewall aceita $ORIGEM_GUARDIAN na porta 80" "FALHA"; FALHAS=$((FALHAS+1))
fi

# 2) a rota existe como exact match, libera a origem e encaminha para o receptor.
CONF=$(nginx -T 2>/dev/null | grep -A 25 "location = $ROTA" || true)
printf '%s' "$CONF" | grep -q . \
  && nota "nginx tem 'location = $ROTA'" "OK" \
  || { nota "nginx tem 'location = $ROTA'" "FALHA"; FALHAS=$((FALHAS+1)); }
printf '%s' "$CONF" | grep -qE "allow[[:space:]]+$ORIGEM_GUARDIAN" \
  && nota "  a location libera $ORIGEM_GUARDIAN" "OK" \
  || { nota "  a location libera $ORIGEM_GUARDIAN" "FALHA"; FALHAS=$((FALHAS+1)); }
DESTINO=$(printf '%s' "$CONF" | grep -oE 'proxy_pass[[:space:]]+http://[^;]+' | head -1 | awk '{print $2}')
if printf '%s' "${DESTINO:-}" | grep -q "18086"; then
  nota "  encaminha para o receptor (${DESTINO})" "OK"
else
  nota "  encaminha para 18086 (achado: ${DESTINO:-nenhum proxy_pass})" "FALHA"; FALHAS=$((FALHAS+1))
fi

# 3) o receptor atende a rota. Conferido direto na porta dele: o nginx ja foi conferido acima, e
#    um GET de 127.0.0.1 passando pelo nginx cairia na allow-list da location.
HTTP=$(curl -s -o /dev/null -m 5 -w '%{http_code}' "http://$RECEPTOR$ROTA" 2>/dev/null || echo 000)
case "$HTTP" in
  200|400|405) nota "receptor atende em $RECEPTOR (HTTP $HTTP)" "OK" ;;
  *) nota "receptor atende em $RECEPTOR (HTTP $HTTP)" "FALHA"; FALHAS=$((FALHAS+1)) ;;
esac

# 4) reforco: a rota ja registrou chamada real do Guardian.
if [ -s /var/log/nginx/fugates-toledo-route.log ]; then
  nota "log da rota tem $(wc -l < /var/log/nginx/fugates-toledo-route.log) linha(s)" "OK"
else
  nota "log da rota vazio ou ausente" "-"
fi

echo
if [ "$FALHAS" -gt 0 ]; then
  echo "BLOCKED: $FALHAS conferencia(s) nao se sustentaram. Nao vou escrever atestado sem lastro."
  echo "         Corrija o que falhou acima e rode de novo."
  exit 1
fi
echo "-- os atestados se sustentam  OK"

if [ "$MODO" = conferir ]; then
  echo
  echo "Nada foi alterado. Para liberar:"
  echo "   sudo bash $0 instalar --evidencia <REFERENCIA-DA-CONFERENCIA>"
  exit 0
fi

# ---------------------------------------------------------------- instalar
test -n "$EVIDENCIA" || { echo "BLOCKED: --evidencia e obrigatorio; a referencia tem de nomear a conferencia feita"; exit 1; }
case "$EVIDENCIA" in
  ''|*[!A-Za-z0-9_-]*) echo "BLOCKED: --evidencia aceita letras, numeros, - e _"; exit 1 ;;
esac
case "$EVIDENCIA" in
  PENDENTE|NOT_APPROVED|NAO_VALIDADO) echo "BLOCKED: --evidencia nao pode ser marcador de pendencia"; exit 1 ;;
esac
test -n "$BINDING" || { echo "BLOCKED: nenhum drop-in com ExecStart em $DROPDIR"; exit 1; }

echo
echo "=== instalando ==="
cp "$BINDING" "$BINDING.bak.protocolo.$SELO"
echo "-- backup do binding: $BINDING.bak.protocolo.$SELO"

cat > "$ENVPROTO" <<EOF
# Libera a criacao do protocolo apos a criacao do contrato. Escrito por
# habilitar-protocolo-acm.sh em $SELO, depois de conferir, nesta maquina:
#   - regra de aceite para $ORIGEM_GUARDIAN na porta 80, com contador de trafego real;
#   - location exact match '$ROTA' no nginx, liberando essa origem e encaminhando ao receptor;
#   - receptor atendendo a rota em $RECEPTOR.
# Referencia da conferencia: $EVIDENCIA
#
# Carregado como ULTIMO EnvironmentFile do drop-in canonico: EnvironmentFile acumula e o ultimo
# vence por chave, entao estas chaves prevalecem sobre o CallbackApproved=false declarado antes.
AcmR57__Normal__CallbackApproved=true
AcmR57__Normal__CallbackUrl=$URL
AcmR57__Normal__CallbackEvidence=$EVIDENCIA
AcmR57__Normal__CallbackBinding__Environment=HML
AcmR57__Normal__CallbackBinding__Url=$URL
AcmR57__Normal__CallbackBinding__SourceCidr=$CIDR
AcmR57__Normal__CallbackBinding__SourceAllowlistVerified=true
AcmR57__Normal__CallbackBinding__PreCadastroReturnRouteBindingVerified=true
AcmR57__Normal__CallbackBinding__EvidenceReference=$EVIDENCIA
EOF
chown root:michael "$ENVPROTO"; chmod 640 "$ENVPROTO"
echo "-- $ENVPROTO criado"

grep -qF "EnvironmentFile=$ENVPROTO" "$BINDING" || printf 'EnvironmentFile=%s\n' "$ENVPROTO" >> "$BINDING"
echo "-- ligado como ultimo EnvironmentFile de $(basename "$BINDING")"

restaurar() {
  cp "$BINDING.bak.protocolo.$SELO" "$BINDING"
  rm -f "$ENVPROTO"
  systemctl daemon-reload
  systemctl start "$SERVICO" || true
}

systemctl daemon-reload
systemctl restart "$SERVICO"
sleep 6
if ! systemctl is-active --quiet "$SERVICO"; then
  echo "   BLOCKED: o motor nao subiu. Ultimas linhas:"
  journalctl -u "$SERVICO" --since "-2 min" --no-pager -q 2>/dev/null \
    | grep -iE "exception|fail|erro|invalid|required|callback" | tail -10 | sed 's/^/     /'
  restaurar
  echo "   Revertido: binding restaurado e motor reiniciado."
  exit 1
fi
echo "-- motor ATIVO"

# Conferencia do EFEITO: o processo em execucao tem de estar vendo CallbackApproved=true.
PID=$(systemctl show -p MainPID --value "$SERVICO")
if [ -n "$PID" ] && [ "$PID" != 0 ] && [ -r "/proc/$PID/environ" ]; then
  tr '\0' '\n' < "/proc/$PID/environ" | grep -qx "AcmR57__Normal__CallbackApproved=true" \
    && echo "-- o processo em execucao ve CallbackApproved=true  OK" \
    || { echo "   BLOCKED: o processo NAO ve CallbackApproved=true. Outra coisa tem precedencia."
         echo "            Revertendo."; restaurar; exit 1; }
fi

echo
echo "-- aguardando o motor varrer a fila (60s)..."
sleep 60
echo
echo "=== protocolos: quem saiu do bloqueio de callback ==="
sudo -u michael env PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w \
  'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc \
  "select v.numero_contrato||'  '||(v.payload_contrato->>'Centro')||'  codigo='||coalesce(v.guardian_codigo_confirmado,'-')||'  protocolo='||coalesce(v.guardian_protocolo_id::text,'-')||'  ticket='||coalesce(v.guardian_numero_ticket,'-')||'  '||coalesce(p.erro_codigo,'(sem erro)')
     from fugates.contrato_sap_toledo_versao v
     left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=v.integracao_id
    where v.guardian_codigo_confirmado is not null
    order by v.numero_contrato" | sed 's/^/   /' || true

echo
echo "Para desfazer: sudo bash $0 reverter"
