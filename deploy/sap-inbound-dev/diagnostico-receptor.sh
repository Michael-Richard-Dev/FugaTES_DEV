#!/usr/bin/env bash
# Leitura apenas. Nao altera nada e nao imprime segredos: so os nomes das chaves e, para as tres
# que decidem o modo de admissao, o valor (nenhuma delas e credencial).
set -euo pipefail
test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }
ENV=/etc/fugates/callback-http/runtime.env
test -f "$ENV" || { echo "BLOCKED: $ENV nao existe"; exit 1; }

echo "=== chaves presentes em runtime.env (apenas nomes):"
grep -oE "^[A-Za-z0-9_]+(__[A-Za-z0-9_]+)*" "$ENV" | sed 's/^/   /'

echo
echo "=== valores das tres chaves que decidem o modo de admissao:"
for K in SapContractInbound__AutomaticProcessingEnabled SapContractInbound__SecurityMode MotorIntegracao__AcmInboundWindow__Enabled; do
  V=$(grep -E "^$K=" "$ENV" | head -1 | cut -d= -f2- || true)
  printf '   %-55s %s\n' "$K" "${V:-(ausente)}"
done

echo
echo "=== unidade do receptor:"
systemctl list-units --type=service --all --no-pager 2>/dev/null | grep -i "callback\|inbound" | sed 's/^/   /' || true
