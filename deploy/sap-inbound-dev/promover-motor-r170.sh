#!/usr/bin/env bash
# R170 - Promove o motor FugaTES para um release que conhece contrato de VENDA (AC3S/S/40).
#
# Motivo: o release em execucao (R165Y_PARTNER_20261025_R3, de 25/10) foi compilado antes da
# extensao R170. Seu binario contem AC3P e 309837, mas NAO contem AC3S nem 309838. Por isso o
# gate R57 recusava o contrato 0000001160 com R165U_BUSINESS_SCOPE_NOT_APPROVED, antes de
# qualquer chamada externa - comprovado na projecao_resultado_fluxo_mvp em 29/10 13:27:29.
#
# O release antigo NAO e alterado: a reversao apenas reaponta a unit de volta para ele.
# Este script nao inicia o motor; quem controla partida e parada e o cadastro-1160-guardian.sh.
#
# Uso:  sudo bash promover-motor-r170.sh instalar
#       sudo bash promover-motor-r170.sh reverter
set -euo pipefail

MODO=${1:-}
ORIGEM=/home/michael/r170pub
RELEASES=/opt/fugates/releases/FugaTES.ACM
NOVO=$RELEASES/R170_VENDA_20261029_R1
ANTIGO=$RELEASES/R165Y_PARTNER_20261025_R3
SERVICO=fugates-acm-motor-dev
UNIT=/etc/systemd/system/$SERVICO.service
SELO=$(date -u +%Y%m%d_%H%M%SZ)

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }
case "$MODO" in instalar|reverter) ;; *) echo "uso: sudo bash $0 instalar|reverter"; exit 1 ;; esac

# Conta ocorrencias de uma string em assembly .NET: literais ficam em UTF-16, por isso os nulos
# sao removidos antes do grep. Metodo validado nos dois binarios antes de ser usado como criterio.
contar() { tr -d '\000' < "$1" | grep -c "$2" || true; }

# ---------------------------------------------------------------- reverter
if [ "$MODO" = reverter ]; then
  test -d "$ANTIGO/runtime/host" || { echo "BLOCKED: release antigo ausente em $ANTIGO"; exit 1; }
  ULTIMO=$(ls -1t "$UNIT".bak.r170.* 2>/dev/null | head -1 || true)
  if [ -n "$ULTIMO" ]; then
    echo "-- restaurando unit a partir de $ULTIMO"
    cp "$ULTIMO" "$UNIT"
  else
    echo "-- sem backup; reapontando a unit para o release antigo por substituicao"
    sed -i "s|$NOVO/runtime/host|$ANTIGO/runtime/host|g" "$UNIT"
  fi
  systemctl is-active --quiet "$SERVICO" && systemctl stop "$SERVICO" || true
  systemctl daemon-reload
  echo "-- unit aponta agora para:"
  grep -E "^(WorkingDirectory|ExecStart)=" "$UNIT" | sed 's/^/   /'
  echo "Revertido. O release R170 permanece em disco e nao foi apagado."
  exit 0
fi

# ---------------------------------------------------------------- instalar
echo "=== R170 - promocao do motor para aceitar contrato de venda ==="

test -d "$ORIGEM/runtime/host" || { echo "BLOCKED: pacote nao encontrado em $ORIGEM/runtime/host"; exit 1; }
FONTE=$ORIGEM/runtime/host/FugaTES.Aplicacao.dll
test -f "$FONTE" || { echo "BLOCKED: $FONTE ausente"; exit 1; }

# 1) O pacote tem de ser realmente o corrigido. Sem isso a promocao nao resolve nada.
echo
echo "-- verificacao do pacote recebido:"
for S in AC3P AC3S 309837 309838; do printf "   %-8s %s\n" "$S" "$(contar "$FONTE" "$S")"; done
for S in AC3S 309838; do
  test "$(contar "$FONTE" "$S")" -ge 1 || { echo "   BLOCKED: pacote nao contem $S; nao e o binario R170"; exit 1; }
done
echo "   pacote confere: conhece compra e venda  OK"

# 2) Confirma o diagnostico no release que esta rodando, para o registro da operacao.
echo
echo "-- release em execucao ($(basename "$ANTIGO")):"
for S in AC3P AC3S 309837 309838; do printf "   %-8s %s\n" "$S" "$(contar "$ANTIGO/runtime/host/FugaTES.Aplicacao.dll" "$S")"; done

# 3) O motor nao pode estar rodando durante a troca.
if systemctl is-active --quiet "$SERVICO"; then
  echo
  echo "-- motor ativo; parando antes da troca"
  systemctl stop "$SERVICO"
fi

# 4) Instala o novo release ao lado do antigo. Nada e sobrescrito.
echo
if [ -d "$NOVO/runtime/host" ]; then
  echo "-- release $NOVO ja existe; conteudo sera substituido"
  rm -rf "$NOVO/runtime/host"
fi
install -d -o root -g root -m 755 "$NOVO/runtime/host"
cp -a "$ORIGEM/runtime/host/." "$NOVO/runtime/host/"
chown -R root:root "$NOVO"
echo "-- instalado em $NOVO/runtime/host ($(ls -1 "$NOVO/runtime/host" | wc -l) arquivos)"

# 5) O conjunto de arquivos tem de bater com o release aprovado, para nao faltar dependencia.
FALTANDO=$(comm -13 <(ls -1 "$NOVO/runtime/host" | sort) <(ls -1 "$ANTIGO/runtime/host" | sort))
test -z "$FALTANDO" || { echo "   BLOCKED: arquivos presentes no release aprovado e ausentes no novo:"; echo "$FALTANDO"; exit 1; }
echo "-- nenhum arquivo do release aprovado ficou de fora  OK"

# 6) Reaponta a unit. Somente as duas linhas de caminho; EnvironmentFile e lock ficam intactos.
cp "$UNIT" "$UNIT.bak.r170.$SELO"
echo "-- backup da unit: $UNIT.bak.r170.$SELO"
sed -i "s|$ANTIGO/runtime/host|$NOVO/runtime/host|g" "$UNIT"
grep -q "$NOVO/runtime/host" "$UNIT" || { echo "BLOCKED: unit nao foi reapontada"; cp "$UNIT.bak.r170.$SELO" "$UNIT"; exit 1; }
grep -q "$ANTIGO/runtime/host" "$UNIT" && { echo "BLOCKED: unit ainda referencia o release antigo"; cp "$UNIT.bak.r170.$SELO" "$UNIT"; exit 1; }
systemctl daemon-reload

echo
echo "-- unit aponta agora para:"
grep -E "^(WorkingDirectory|ExecStart)=" "$UNIT" | sed 's/^/   /'
echo "-- travas preservadas:"
grep -E "^(EnvironmentFile|ExecStartPre)=" "$UNIT" | sed 's/^/   /'

echo
echo "Promocao concluida. O motor NAO foi iniciado."
echo "Proximo passo: sudo bash /home/michael/cadastro-1160-guardian.sh conferir"
echo "Para reverter:  sudo bash $0 reverter"
