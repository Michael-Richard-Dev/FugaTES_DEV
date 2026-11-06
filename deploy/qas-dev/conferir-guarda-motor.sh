#!/usr/bin/env bash
# A cadeia de conexao do motor SD passaria pela guarda nova?
#
# NAO ALTERA NADA. Nao reinicia servico, nao publica, nao escreve arquivo.
#
# POR QUE EXISTE. Em 06/11/2026 o motor ganhou uma guarda de banco que ele NAO tinha: a cadeia
# precisa apontar para o banco do MESMO ambiente do SAP, com o papel daquele ambiente, na porta
# 5433, schema fugates, host loopback. A regra foi escrita a partir do que o codigo deveria
# exigir - nao a partir do que o servico tem configurado hoje.
#
# Se divergir em qualquer campo, o motor RECUSA SUBIR na proxima publicacao, e o DEV110 para de
# criar remessa. Isto confere antes, com o servico intocado.
#
# NENHUMA SENHA E IMPRESSA. Password e Passfile sao reduzidos a "definido"/"ausente"; o valor
# nunca sai daqui.
#
# Uso:  sudo ./conferir-guarda-motor.sh
set -euo pipefail

SERVICO="${SERVICO:-fugates-sd-protocolo}"
CHAVE="${CHAVE:-SdProtocolo__ConnectionString}"

[ "$(id -u)" = 0 ] || { echo "Rode com sudo (o EnvironmentFile e do root)."; exit 1; }

# Os EnvironmentFile da unidade, na ordem em que o systemd os aplica - o ultimo a definir vence.
ARQUIVOS="$(systemctl show "$SERVICO" -p EnvironmentFiles --value 2>/dev/null \
            | tr ' ' '\n' | sed 's/ (ignore_errors=.*//' | grep -v '^$' || true)"

[ -n "$ARQUIVOS" ] || { echo "O servico $SERVICO nao tem EnvironmentFile."; exit 1; }

echo "servico .......... $SERVICO"
echo "arquivos de ambiente, na ordem:"
echo "$ARQUIVOS" | sed 's/^/   /'
echo

CADEIA=""
ONDE=""
while read -r f; do
  [ -f "$f" ] || continue
  v="$(grep -E "^[[:space:]]*${CHAVE}=" "$f" 2>/dev/null | tail -1 | cut -d= -f2- || true)"
  if [ -n "$v" ]; then CADEIA="$v"; ONDE="$f"; fi
done <<< "$ARQUIVOS"

[ -n "$CADEIA" ] || { echo "Nao achei $CHAVE em nenhum deles."; exit 1; }
echo "a cadeia vem de ..... $ONDE"
echo

# Um campo da cadeia, por nome, sem imprimir a cadeia inteira.
campo() {
  printf '%s' "$CADEIA" | tr ';' '\n' \
    | grep -iE "^[[:space:]]*$1[[:space:]]*=" | tail -1 | cut -d= -f2- | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

HOST="$(campo 'Host')"
PORTA="$(campo 'Port')"
BANCO="$(campo 'Database')"
USUARIO="$(campo 'Username')"
SCHEMA="$(campo 'Search ?Path')"
TEM_SENHA="$([ -n "$(campo 'Password')" ] && echo definido || echo ausente)"
TEM_PASSFILE="$([ -n "$(campo 'Passfile')" ] && echo definido || echo ausente)"

# Vazio e informacao: o Npgsql tem padroes, e a guarda compara o valor EFETIVO.
mostrar() { printf '  %-14s %-28s %s\n' "$1" "${2:-(ausente)}" "$3"; }

echo "o que a guarda nova exige, e o que esta configurado:"
mostrar "Host"        "$HOST"     "exige 127.0.0.1 ou localhost"
mostrar "Port"        "$PORTA"    "exige 5433"
mostrar "Database"    "$BANCO"    "exige o banco do ambiente SAP do servico"
mostrar "Username"    "$USUARIO"  "exige o papel SD daquele ambiente"
mostrar "Search Path" "$SCHEMA"   "exige fugates"
mostrar "Password"    "$TEM_SENHA"   "(valor nunca impresso)"
mostrar "Passfile"    "$TEM_PASSFILE" "(caminho nunca impresso)"
echo

FALHAS=0
conferir() {
  if [ "$2" = "$3" ]; then printf '  OK      %s\n' "$1"
  else printf '  RECUSA  %s: configurado "%s", exigido "%s"\n' "$1" "${2:-(ausente)}" "$3"; FALHAS=$((FALHAS+1)); fi
}

echo "veredito:"
case "$HOST" in 127.0.0.1|localhost) printf '  OK      Host\n';; *) printf '  RECUSA  Host: configurado "%s", exigido 127.0.0.1 ou localhost\n' "${HOST:-(ausente)}"; FALHAS=$((FALHAS+1));; esac
conferir "Port" "$PORTA" "5433"
conferir "Search Path" "$SCHEMA" "fugates"

# O par banco+papel depende do ambiente SAP deste servico, que vem da mesma configuracao.
SAP_URL="$(printf '%s' "$(while read -r f; do [ -f "$f" ] && grep -E '^[[:space:]]*Sap__BaseUrl=' "$f" 2>/dev/null | tail -1 | cut -d= -f2-; done <<< "$ARQUIVOS")" | tail -1)"
SAP_CLI="$(printf '%s' "$(while read -r f; do [ -f "$f" ] && grep -E '^[[:space:]]*Sap__SapClient=' "$f" 2>/dev/null | tail -1 | cut -d= -f2-; done <<< "$ARQUIVOS")" | tail -1)"
case "$SAP_CLI" in
  110) conferir "Database" "$BANCO" "fugates_dev";     conferir "Username" "$USUARIO" "fugates_sd_app";;
  120) conferir "Database" "$BANCO" "fugates_qas";     conferir "Username" "$USUARIO" "fugates_qas_sd_app";;
  *)   printf '  ?       Sap__SapClient="%s" nao e 110 nem 120; nao da para dizer o par esperado\n' "${SAP_CLI:-(ausente)}"; FALHAS=$((FALHAS+1));;
esac
echo
echo "  (Sap__BaseUrl configurado: ${SAP_URL:-(ausente)})"
echo

if [ "$FALHAS" = 0 ]; then
  echo "A guarda nova ACEITARIA esta configuracao. A proxima publicacao do motor pode subir."
else
  echo "$FALHAS campo(s) fariam o motor RECUSAR SUBIR. NAO PUBLIQUE o motor antes de resolver:"
  echo "  - ou o EnvironmentFile passa a ter os valores exigidos;"
  echo "  - ou a guarda esta exigindo o que o ambiente real nao usa, e quem muda sou eu."
  exit 1
fi
