#!/usr/bin/env bash
# Publica um release do motor ACM (fugates-acm-motor-dev) E abre o cadastro automatico para os
# centros 3011 e 3008 - as duas coisas num passo so, de proposito.
#
# POR QUE UM SCRIPT SO. Na primeira tentativa isto eram dois: um trocava o release, outro abria os
# centros. O que trocava o release editava o ARQUIVO PRINCIPAL da unit e anunciava sucesso; mas a
# unit tem drop-ins, e o ultimo deles - chamado "zzzzzzzzzz-..." - redefine WorkingDirectory e
# ExecStart. O release que o systemd realmente usava era outro. O anuncio estava correto sobre o
# arquivo editado e errado sobre a realidade. Separar as duas coisas foi o que permitiu que elas
# discordassem; por isso agora o mesmo script faz as duas e confere o efeito, nao o arquivo.
#
# O QUE ELE TOCA, E SO ISSO:
#   1. o drop-in de MAIOR precedencia que define ExecStart (o binding canonico, qualquer que seja o
#      nome dele): troca APENAS o caminho do release em WorkingDirectory e ExecStart, preservando o
#      resto da linha e o resto do arquivo;
#   2. acrescenta a esse mesmo drop-in uma linha EnvironmentFile apontando para centros.env;
#   3. cria centros.env com as duas chaves do recorte.
#
# NAO cria drop-in novo. Disputar precedencia acrescentando mais um "z" ao nome e corrida de nomes,
# nao solucao - e esta unit e a prova do que isso produz.
#
# POR QUE centros.env E NAO um arquivo existente: EnvironmentFile acumula e o ULTIMO vence por
# chave. Escrever o recorte num arquivo carregado antes dos env dos drop-ins deixaria a escrita a
# merce deles. Entrando como ultimo EnvironmentFile do drop-in canonico, centros.env vence. A chave
# antiga MotorIntegracao__AcmAutomatico__Centro pode ficar onde esta: no codigo, Centros tem
# precedencia sobre Centro (ver AcmAutomaticoOptions.CentrosEfetivos). Nada de outro trabalho e
# alterado alem da linha acrescentada ao drop-in.
#
# Uso:  sudo bash publicar-motor-acm.sh conferir   <- nao altera NADA; leia antes de instalar
#       sudo bash publicar-motor-acm.sh instalar [/caminho/do/pacote]
#       sudo bash publicar-motor-acm.sh reverter
set -euo pipefail

MODO=${1:-}
ORIGEM=${2:-/home/michael/r172}
SERVICO=fugates-acm-motor-dev
RELEASES=/opt/fugates/releases/FugaTES.ACM
NOME=R172_CENTROS_20261104
DESTINO=$RELEASES/$NOME/runtime/host
ENVDIR=/etc/fugates/motor-dev
ENVCENTROS=$ENVDIR/centros.env
DROPDIR=/etc/systemd/system/$SERVICO.service.d
SELO=$(date -u +%Y%m%d_%H%M%SZ)
MARCAS=(CentrosEfetivos EhPermitido)

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }
case "$MODO" in conferir|instalar|reverter) ;; *) echo "uso: sudo bash $0 conferir|instalar|reverter"; exit 1 ;; esac

contar() { tr -d '\000' < "$1" | grep -c "$2" || true; }
efetivo() { systemctl show -p "$1" --value "$SERVICO" 2>/dev/null; }

# O binding canonico e o drop-in de maior precedencia que define ExecStart. Ordem lexical: o
# ultimo da lista vence. Descobrir em vez de fixar o nome - o nome atual e historico e mente.
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
  ALVO=${BINDING:-}
  test -n "$ALVO" || { echo "BLOCKED: nenhum drop-in com ExecStart em $DROPDIR"; exit 1; }
  ULTIMO=$(ls -1t "$ALVO".bak.centros.* 2>/dev/null | head -1 || true)
  test -n "$ULTIMO" || { echo "BLOCKED: nenhum backup .bak.centros.* de $ALVO"; exit 1; }
  cp "$ULTIMO" "$ALVO"
  rm -f "$ENVCENTROS"
  systemctl daemon-reload
  systemctl restart "$SERVICO"
  sleep 6
  echo "-- drop-in restaurado de $ULTIMO; centros.env removido"
  echo "-- ExecStart efetivo: $(efetivo ExecStart | sed 's/.*argv\[\]=//; s/ ;.*//')"
  systemctl is-active --quiet "$SERVICO" && echo "-- motor ATIVO" \
    || { echo "   BLOCKED: motor nao subiu apos reverter"; journalctl -u "$SERVICO" --since "-2 min" --no-pager -q | tail -8 | sed 's/^/     /'; exit 1; }
  exit 0
fi

# ---------------------------------------------------------------- leitura do estado real
echo "=== estado efetivo do $SERVICO ==="
EXEC_ATUAL=$(efetivo ExecStart | sed 's/.*argv\[\]=//; s/ ;.*//')
WD_ATUAL=$(efetivo WorkingDirectory)
echo "   unit principal ..... $(efetivo FragmentPath)"
echo "   drop-ins ........... $(efetivo DropInPaths | tr ' ' '\n' | sed 's|.*/||' | paste -sd' ')"
echo "   binding canonico ... ${BINDING:-(nenhum)}"
echo "   WorkingDirectory ... ${WD_ATUAL:-(vazio)}"
echo "   ExecStart .......... ${EXEC_ATUAL:-(vazio)}"
test -n "$BINDING" || { echo "BLOCKED: nenhum drop-in define ExecStart; este script nao sabe onde escrever"; exit 1; }
test -n "$WD_ATUAL" || { echo "BLOCKED: nao consegui ler o WorkingDirectory efetivo"; exit 1; }

echo
echo "=== EnvironmentFile na ordem em que o systemd carrega (o ultimo vence por chave) ==="
efetivo EnvironmentFiles | tr ' ' '\n' | sed 's/^/   /' | grep -v '^   $' || true

echo
echo "=== onde o recorte esta declarado hoje ==="
grep -rn "MotorIntegracao__AcmAutomatico__Centros\?\(__[0-9]\+\)\?=" "$ENVDIR"/*.env 2>/dev/null \
  | sed 's|^|   |' || echo "   (nenhuma declaracao de centro encontrada)"

echo
echo "=== binario em execucao ==="
for M in "${MARCAS[@]}"; do
  N=$(contar "$WD_ATUAL/FugaTES.Aplicacao.dll" "$M" 2>/dev/null || echo 0)
  printf '   %-18s %s\n' "$M" "$N"
done

# ---------------------------------------------------------------- o pacote
echo
echo "=== pacote a instalar ==="
FONTE=$ORIGEM/runtime/host
test -f "$FONTE/FugaTES.Aplicacao.dll" || { echo "   BLOCKED: pacote ausente em $FONTE"; exit 1; }
echo "   origem ............. $FONTE ($(ls -1 "$FONTE" | wc -l) itens)"
for M in "${MARCAS[@]}"; do
  N=$(contar "$FONTE/FugaTES.Aplicacao.dll" "$M")
  printf '   %-18s %s\n' "$M" "$N"
  test "$N" -ge 1 || { echo "   BLOCKED: o pacote nao contem '$M'; nao e o binario dos dois centros"; exit 1; }
done
FALTANDO=$(comm -13 <(ls -1 "$FONTE" | sort) <(ls -1 "$WD_ATUAL" | sort))
test -z "$FALTANDO" || { echo "   BLOCKED: itens do release EM USO ausentes no pacote:"; echo "$FALTANDO" | sed 's/^/     /'; exit 1; }
echo "   nada do release em uso ficou de fora  OK"

if [ "$MODO" = conferir ]; then
  echo
  echo "Nada foi alterado. Se o acima estiver certo:  sudo bash $0 instalar"
  exit 0
fi

# ---------------------------------------------------------------- instalar
echo
echo "=== instalando ==="
systemctl is-active --quiet "$SERVICO" && { echo "-- parando o motor"; systemctl stop "$SERVICO"; }

[ -d "$DESTINO" ] && rm -rf "$DESTINO"
install -d -o root -g root -m 755 "$DESTINO"
cp -a "$FONTE/." "$DESTINO/"
chown -R root:root "$RELEASES/$NOME"
echo "-- release em disco: $DESTINO ($(ls -1 "$DESTINO" | wc -l) itens)"

cp "$BINDING" "$BINDING.bak.centros.$SELO"
echo "-- backup do binding: $BINDING.bak.centros.$SELO"

# Troca SO o caminho do release, onde quer que ele apareca no drop-in. O resto da linha - e o resto
# do arquivo, inclusive os EnvironmentFile que ja estavam la - fica como estava.
ANTIGO=$WD_ATUAL
sed -i "s|${ANTIGO}|${DESTINO}|g" "$BINDING"
grep -q "$DESTINO" "$BINDING" || { echo "BLOCKED: o drop-in nao foi reapontado"; cp "$BINDING.bak.centros.$SELO" "$BINDING"; exit 1; }

# centros.env entra como ULTIMO EnvironmentFile deste drop-in, para vencer por chave.
grep -qF "EnvironmentFile=$ENVCENTROS" "$BINDING" || printf 'EnvironmentFile=%s\n' "$ENVCENTROS" >> "$BINDING"

cat > "$ENVCENTROS" <<EOF
# Recorte continuo do ACM nos DOIS centros. Escrito por publicar-motor-acm.sh em $SELO.
#
# Carregado como ULTIMO EnvironmentFile do drop-in canonico, de proposito: EnvironmentFile acumula
# e o ultimo vence por chave. A chave antiga MotorIntegracao__AcmAutomatico__Centro pode continuar
# declarada em outro arquivo - no codigo, Centros tem precedencia sobre Centro.
#
# O campo adicional 1002 do Guardian leva o centro do PROPRIO contrato: 3011 vai 3011, 3008 vai
# 3008. Decisao do operador em 04/11/2026.
MotorIntegracao__AcmAutomatico__Centros__0=3011
MotorIntegracao__AcmAutomatico__Centros__1=3008
EOF
chown root:michael "$ENVCENTROS"; chmod 640 "$ENVCENTROS"
echo "-- $ENVCENTROS criado e ligado ao binding"

restaurar() {
  cp "$BINDING.bak.centros.$SELO" "$BINDING"
  rm -f "$ENVCENTROS"
  systemctl daemon-reload
  systemctl start "$SERVICO" || true
}

systemctl daemon-reload
systemctl start "$SERVICO"
sleep 6

# A conferencia e do EFEITO, nao do arquivo: o que o systemd resolveu e o que o binario contem.
EXEC_NOVO=$(efetivo ExecStart | sed 's/.*argv\[\]=//; s/ ;.*//')
WD_NOVO=$(efetivo WorkingDirectory)
echo
echo "-- ExecStart efetivo: $EXEC_NOVO"
case "$EXEC_NOVO" in
  *"$DESTINO"*) echo "-- o systemd resolveu para o release novo  OK" ;;
  *) echo "   BLOCKED: o ExecStart efetivo NAO e o release novo. Outra coisa tem precedencia."
     echo "            Revertendo."; restaurar; exit 1 ;;
esac
for M in "${MARCAS[@]}"; do
  N=$(contar "$WD_NOVO/FugaTES.Aplicacao.dll" "$M")
  test "$N" -ge 1 || { echo "   BLOCKED: o binario em execucao nao contem '$M'. Revertendo."; restaurar; exit 1; }
done
echo "-- o binario em execucao conhece os dois centros  OK"

if ! systemctl is-active --quiet "$SERVICO"; then
  echo "   BLOCKED: o motor nao subiu. Ultimas linhas:"
  journalctl -u "$SERVICO" --since "-2 min" --no-pager -q 2>/dev/null \
    | grep -iE "exception|fail|erro|invalid|required|scope" | tail -10 | sed 's/^/     /'
  restaurar
  echo "   Revertido: binding restaurado e motor reiniciado no release anterior."
  exit 1
fi
echo "-- motor ATIVO"

echo
echo "-- aguardando o motor varrer a fila (45s)..."
sleep 45
echo
echo "=== 1207 e 1208, e o que mais e de 3008 ==="
sudo -u michael env PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w \
  'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc \
  "select i.chave_negocio||'  '||(v.payload_contrato->>'Centro')||'  '||i.estado||'  '||coalesce(p.etapa_funcional,'(sem etapa)')||'  '||coalesce(p.erro_codigo,'-')
     from fugates.integracao i
     join fugates.contrato_sap_toledo_versao v on v.integracao_id=i.id
     left join fugates.projecao_resultado_fluxo_mvp p on p.integracao_id=i.id
    where i.operacao='SAP_CONTRATO_TOLEDO' and v.payload_contrato->>'Centro'='3008'
    order by 1" | sed 's/^/   /' || true

echo
echo "Para desfazer: sudo bash $0 reverter"
