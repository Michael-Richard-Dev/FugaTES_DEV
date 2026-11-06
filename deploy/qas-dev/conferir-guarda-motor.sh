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

# SAIR CALADO E PROIBIDO. Em 06/11/2026 a primeira versao deste script morreu sem dizer nada:
# "campo()" termina num grep, que sai com 1 quando nao acha, e com set -e isso derrubava tudo na
# primeira atribuicao. Quem rodou viu meia saida e nenhum erro - que e pior do que uma falha.
#
# Este trap transforma qualquer saida inesperada numa mensagem. Foi o quinto verificador furado
# do dia; o padrao e sempre o mesmo - a ferramenta falha, nao avisa, e o silencio parece sucesso.
#
# CONCLUIDO=1 marca que houve VEREDITO - inclusive um negativo. "Nao tem EnvironmentFile" e uma
# resposta, nao uma queda, e no primeiro ensaio o trap acusava defeito proprio justamente nela.
# Falso alarme corroi a confianca no aviso tanto quanto o silencio.
CONCLUIDO=0
trap '[ "$CONCLUIDO" = 1 ] || echo "
INTERROMPIDO na linha $LINENO, sem veredito. Isto e um defeito DESTE script, nao do ambiente:
nada foi conferido. Me mande esta saida." >&2' EXIT

# Os EnvironmentFile da unidade, na ordem em que o systemd os aplica - o ultimo a definir vence.
#
# O systemd imprime "caminho (ignore_errors=no)", e quebrar por espaco fazia o "(ignore_errors=no)"
# virar uma linha propria na lista - saia na tela como se fosse um arquivo. So caminho absoluto
# entra.
ARQUIVOS="$(systemctl show "$SERVICO" -p EnvironmentFiles --value 2>/dev/null \
            | tr ' ' '\n' | grep -E '^/' || true)"

[ -n "$ARQUIVOS" ] || { echo "O servico $SERVICO nao tem EnvironmentFile."; CONCLUIDO=1; exit 1; }

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

[ -n "$CADEIA" ] || { echo "Nao achei $CHAVE em nenhum deles."; CONCLUIDO=1; exit 1; }
echo "a cadeia vem de ..... $ONDE"
echo

# Um campo da cadeia, por nome, sem imprimir a cadeia inteira.
#
# O "|| true" no fim nao e preguica: campo AUSENTE e uma resposta legitima - o Npgsql tem padroes,
# e a guarda compara o valor efetivo. Sem ele o grep sai com 1, o pipefail propaga e o set -e
# derruba o script na primeira atribuicao, que foi exatamente o que aconteceu.
campo() {
  { printf '%s' "$CADEIA" | tr ';' '\n' \
      | grep -iE "^[[:space:]]*$1[[:space:]]*=" | tail -1 | cut -d= -f2- \
      | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'; } || true
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
# A ultima definicao vence, como no systemd. O "|| true" existe pelo mesmo motivo de campo():
# chave ausente e resposta legitima, e nao motivo para o script morrer.
chave_do_ambiente() {
  local achado="" f v
  while read -r f; do
    [ -f "$f" ] || continue
    v="$( { grep -E "^[[:space:]]*$1=" "$f" 2>/dev/null | tail -1 | cut -d= -f2-; } || true )"
    [ -n "$v" ] && achado="$v"
  done <<< "$ARQUIVOS"
  printf '%s' "$achado"
}

SAP_URL="$(chave_do_ambiente 'Sap__BaseUrl')"
SAP_CLI="$(chave_do_ambiente 'Sap__SapClient')"
case "$SAP_CLI" in
  110) conferir "Database" "$BANCO" "fugates_dev";     conferir "Username" "$USUARIO" "fugates_sd_app";;
  120) conferir "Database" "$BANCO" "fugates_qas";     conferir "Username" "$USUARIO" "fugates_qas_sd_app";;
  *)   printf '  ?       Sap__SapClient="%s" nao e 110 nem 120; nao da para dizer o par esperado\n' "${SAP_CLI:-(ausente)}"; FALHAS=$((FALHAS+1));;
esac
echo
echo "  (Sap__BaseUrl configurado: ${SAP_URL:-(ausente)})"
echo

# Chegou ao veredito: o trap nao precisa mais avisar de saida calada.
CONCLUIDO=1

if [ "$FALHAS" = 0 ]; then
  echo "A guarda nova ACEITARIA esta configuracao. A proxima publicacao do motor pode subir."
else
  echo "$FALHAS campo(s) fariam o motor RECUSAR SUBIR. NAO PUBLIQUE o motor antes de resolver:"
  echo "  - ou o EnvironmentFile passa a ter os valores exigidos;"
  echo "  - ou a guarda esta exigindo o que o ambiente real nao usa, e quem muda sou eu."
  exit 1
fi
