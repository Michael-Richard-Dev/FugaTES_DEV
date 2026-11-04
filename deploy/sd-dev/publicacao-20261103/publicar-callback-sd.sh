#!/usr/bin/env bash
# Publica o receptor compartilhado (Toledo/ACM + devolutiva SD) no servico que ja atende o
# Guardian, usando o BINDING CANONICO - e so ele.
#
# Por que este script existe, e nao o instalar-devolutiva-sd.sh ao lado: aquele acrescentava um
# drop-in proprio com mais "z" no nome para vencer a precedencia dos outros onze. Isso e uma
# corrida de nomes, nao uma solucao, e desde a consolidacao de 02/11/2026 ha um unico binding,
# o 90-fugates-runtime.conf. Aqui nao se cria drop-in nenhum: troca-se o CAMINHO DO RELEASE
# dentro daquele arquivo, preservando todo o resto da linha - os argumentos, inclusive
# "--urls" e "--SdInbound:DevolutivaEnabled=true", ja custaram uma madrugada quando sumiram.
#
# Uso:  sudo ./publicar-callback-sd.sh {conferir|ensaio|instalar|reverter}
#
#   conferir  nao altera nada: mostra servico, release ativo, binding e as duas rotas.
#   ensaio    nao altera nada: mostra exatamente o ExecStart que seria escrito.
#   instalar  extrai o pacote, aponta o binding, reinicia e CONFERE as duas rotas.
#             Qualquer falha volta sozinha para o release anterior.
#   reverter  volta para o release anterior registrado.
set -euo pipefail

SERVICO=fugates-toledo-callback
PORTA=18086
ROTA_SD=/api/guardian/set26/callback/venda
ROTA_ACM=/api/guardian/set26/callback/recebimento
PACOTE=${PACOTE:-/home/michael/fugates-sd-devolutiva.tgz}
SHA_ESPERADO='94c29efb5cc7b14334b915f8fee6938ec4cfe5cf466d1fc938b0a78aaf88d80f'
RAIZ=/opt/fugates/releases/FugaTES.Callback.Http
BINDING=/etc/systemd/system/$SERVICO.service.d/90-fugates-runtime.conf
ANOTACAO=/etc/fugates/callback-sd-release-anterior
BACKUPS=/var/backups/fugates/toledo

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

# ---------------------------------------------------------------------------- leitura do estado

# O ExecStart EFETIVO, como o systemd o resolveu - nao o que algum arquivo diz isoladamente.
argv_atual() {
  systemctl show "$SERVICO" -p ExecStart --value \
    | sed -n 's/.*argv\[\]=\(.*\) ; ignore_errors.*/\1/p' | head -1
}
release_atual() { argv_atual | grep -oE "$RAIZ/[^/ ]+" | head -1 | xargs -r basename; }
usuario() { systemctl show "$SERVICO" -p User --value; }

# GET nas duas rotas. O receptor responde ao GET sem gravar nada - e a prova de que a rota
# existe e esta servida pelo processo novo.
rota_ok() {
  curl -fsS --max-time 10 "http://127.0.0.1:$PORTA$1" >/dev/null 2>&1
}

mostrar() {
  echo "== servico ......... $SERVICO ($(systemctl is-active "$SERVICO" || true))"
  echo "== usuario ......... $(usuario)"
  echo "== release ativo ... ${1:-$(release_atual)}"
  echo "== binding ......... $BINDING"
  if [ -f "$BINDING" ]; then
    echo "-- ExecStart no binding:"
    grep -n '^ExecStart' "$BINDING" | sed 's/^/   /'
  else
    echo "   AUSENTE. Este script nao sabe publicar sem o binding canonico."
  fi
  for rota in "$ROTA_SD" "$ROTA_ACM"; do
    if rota_ok "$rota"; then echo "== GET $rota ... OK"; else echo "== GET $rota ... SEM RESPOSTA"; fi
  done
}

# ------------------------------------------------------------------------------------ conferir

case "${1:-}" in
conferir)
  mostrar
  if [ -f "$PACOTE" ]; then
    echo "== pacote .......... $PACOTE"
    echo "   sha256 $(sha256sum "$PACOTE" | cut -d' ' -f1)"
    echo "   esperado $SHA_ESPERADO"
  else
    echo "== pacote .......... AUSENTE em $PACOTE"
  fi
  [ -f "$ANOTACAO" ] && echo "== release anterior anotado: $(cat "$ANOTACAO")"
  exit 0
  ;;
esac

# -------------------------------------------------------------- validacoes comuns de publicacao

ATUAL=$(release_atual || true)
[ -n "$ATUAL" ] || { echo "Nao consegui ler o release ativo do ExecStart. Nada foi alterado."; exit 1; }
[ -f "$BINDING" ] || { echo "Binding canonico ausente: $BINDING. Nada foi alterado."; exit 1; }
grep -q '^ExecStart' "$BINDING" || { echo "O binding nao define ExecStart. Nada foi alterado."; exit 1; }

case "${1:-}" in
reverter)
  [ -f "$ANOTACAO" ] || { echo "Nao ha release anterior anotado. Nada foi alterado."; exit 1; }
  NOVO=$(cat "$ANOTACAO")
  [ -d "$RAIZ/$NOVO" ] || { echo "O release anotado nao existe mais: $NOVO. Nada foi alterado."; exit 1; }
  ;;
instalar)
  [ -f "$PACOTE" ] || { echo "Pacote ausente: $PACOTE. Nada foi alterado."; exit 1; }
  SHA=$(sha256sum "$PACOTE" | cut -d' ' -f1)
  [ "$SHA" = "$SHA_ESPERADO" ] || {
    echo "SHA do pacote nao confere."; echo "  recebido $SHA"; echo "  esperado $SHA_ESPERADO"
    echo "Nada foi alterado."; exit 1; }
  NOVO=SD_ACM_DATAS_$(date -u +%Y%m%d_%H%M%SZ)
  ;;
ensaio)
  NOVO=SD_ACM_DATAS_ENSAIO
  ;;
*)
  echo "Uso: sudo $0 {conferir|ensaio|instalar|reverter}"; exit 1
  ;;
esac

ARGV_ATUAL=$(argv_atual)
# Troca SO o caminho do release. O resto da linha - executavel, --urls, as chaves de
# configuracao - sai daqui intacto, porque e dele que o servico depende para subir.
ARGV_NOVO=$(printf '%s' "$ARGV_ATUAL" | sed "s#$RAIZ/[^/ ]*#$RAIZ/$NOVO#g")
[ "$ARGV_NOVO" != "$ARGV_ATUAL" ] || { echo "A substituicao do release nao mudou nada. Nada foi alterado."; exit 1; }

if [ "${1:-}" = "ensaio" ]; then
  mostrar "$ATUAL"
  echo
  echo "-- ExecStart que SERIA escrito (nada foi alterado):"
  echo "   ExecStart="
  echo "   ExecStart=$ARGV_NOVO"
  exit 0
fi

# ------------------------------------------------------------------------------------ instalar

if [ "${1:-}" = "instalar" ]; then
  echo "== extraindo em $RAIZ/$NOVO"
  install -d -m 0755 "$RAIZ/$NOVO"
  tar -xzf "$PACOTE" -C "$RAIZ/$NOVO"
  # O conjunto de arquivos tem de ser o MESMO do release que esta rodando. Um arquivo a mais ou
  # a menos aqui significa pacote montado de outro jeito - e um runtime pela metade so aparece
  # quando o servico ja esta no ar.
  if ! diff <(cd "$RAIZ/$ATUAL" && ls | sort) <(cd "$RAIZ/$NOVO" && ls | sort) >/dev/null; then
    echo "O pacote tem conjunto de arquivos diferente do release atual:"
    diff <(cd "$RAIZ/$ATUAL" && ls | sort) <(cd "$RAIZ/$NOVO" && ls | sort) || true
    rm -rf "${RAIZ:?}/${NOVO:?}"
    echo "Release novo removido. Nada foi alterado no servico."; exit 1
  fi
  chown -R "$(usuario)":"$(usuario)" "$RAIZ/$NOVO" 2>/dev/null || true
  echo "== 17 arquivos conferem com $ATUAL"
fi

install -d -m 0755 "$BACKUPS"
cp -a "$BINDING" "$BACKUPS/90-fugates-runtime.conf.$(date -u +%Y%m%d_%H%M%SZ)"
echo "$ATUAL" > "$ANOTACAO"

# A reescrita e cirurgica: so as linhas ExecStart do binding canonico.
TMP=$(mktemp)
awk -v novo="ExecStart=$ARGV_NOVO" '
  /^ExecStart=$/ { print; next }
  /^ExecStart=/  { print novo; next }
  { print }
' "$BINDING" > "$TMP"
cat "$TMP" > "$BINDING"
rm -f "$TMP"
systemctl daemon-reload

# Confere o ExecStart EFETIVO antes de reiniciar: se outro drop-in ainda vencer a precedencia,
# o servico subiria com o release antigo e a publicacao pareceria ter funcionado.
if [ "$(release_atual)" != "$NOVO" ]; then
  echo "FALHOU antes de reiniciar: o ExecStart efetivo aponta para $(release_atual)."
  echo "Algum outro drop-in esta vencendo a precedencia. Desfazendo o binding."
  cp -a "$BACKUPS/90-fugates-runtime.conf."* "$BINDING" 2>/dev/null || true
  systemctl daemon-reload
  exit 1
fi
echo "== ExecStart efetivo aponta para $NOVO"

systemctl restart "$SERVICO"
sleep 3

falhou=""
systemctl is-active --quiet "$SERVICO" || falhou="o servico nao subiu"
[ -n "$falhou" ] || rota_ok "$ROTA_SD" || falhou="a rota do SD nao respondeu"
[ -n "$falhou" ] || rota_ok "$ROTA_ACM" || falhou="a rota do ACM nao respondeu"

if [ -n "$falhou" ]; then
  echo "FALHOU depois de reiniciar: $falhou. Voltando para $ATUAL."
  ARGV_VOLTA=$(printf '%s' "$ARGV_NOVO" | sed "s#$RAIZ/$NOVO#$RAIZ/$ATUAL#g")
  TMP=$(mktemp)
  awk -v novo="ExecStart=$ARGV_VOLTA" '
    /^ExecStart=$/ { print; next }
    /^ExecStart=/  { print novo; next }
    { print }
  ' "$BINDING" > "$TMP"
  cat "$TMP" > "$BINDING"
  rm -f "$TMP"
  systemctl daemon-reload
  systemctl restart "$SERVICO"
  journalctl -u "$SERVICO" -n 30 --no-pager || true
  exit 1
fi

echo "== no ar com $NOVO; as duas rotas responderam."
echo "   release anterior anotado em $ANOTACAO ($ATUAL)"
echo "   para voltar:  sudo $0 reverter"
