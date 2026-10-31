#!/usr/bin/env bash
# R168 - Aponta o recebimento SAP ja existente para o PostgreSQL DEV UNIFICADO.
#
# O QUE ESTAVA ACONTECENDO
#   O servico fugates-callback-http recebe os contratos do SAP na URL corporativa atual e grava em
#   Database=fugates_dev, Port=5432 - o cluster LEGADO. O FugaTES DEV110 unificado le
#   fugates_dev na porta 5433, cluster 18/fugatesacmdev. Mesmo nome de base, clusters diferentes:
#   por isso o contrato chegava com HTTP 201 e nao aparecia.
#
# O QUE ESTE SCRIPT FAZ, E SO ISSO
#   runtime.env:  Port=5432            -> Port=5433
#                 Username=fugates_dev_app -> Username=fugates_sap_inbound_app
#   pgpass do servico: acrescenta a linha da nova role, copiando o segredo do pgpass do michael
#                      SEM imprimi-lo.
#   e reinicia o servico.
#
#   A troca de role nao e opcional: no 5433 a fugates_dev_app nao tem login nem INSERT nas tabelas
#   de contrato. A fugates_sap_inbound_app foi criada para exatamente este fim.
#
# CONSEQUENCIA QUE PRECISA ESTAR CLARA
#   A partir daqui os contratos novos caem na base UNIFICADA e deixam de cair na legada 5432.
#   Nao ha gravacao dupla. O que ja esta na 5432 permanece intacto - nada e copiado nem apagado.
#
# ATENCAO: o reinicio tambem passa a executar o release que ja esta configurado no unit
#   (ExecStart = .../R3_3164a22_L1_R1_337EFFB5), no lugar do que esta em memoria desde 22/10.
#   Esse release exige SecurityMode=LoopbackOnly, que ja e o valor atual.
#
# USO (na VM, como michael):
#   sudo bash apontar-recebimento-sap-para-dev-unificado.sh conferir
#   sudo bash apontar-recebimento-sap-para-dev-unificado.sh aplicar
#   sudo bash apontar-recebimento-sap-para-dev-unificado.sh reverter

set -euo pipefail

ENV_FILE=/etc/fugates/callback-http/runtime.env
PGPASS_SVC=/etc/fugates/callback-http/pgpass
PGPASS_ORIGEM=/home/michael/.fugates-sap-inbound/pgpass
ROLE_NOVA=fugates_sap_inbound_app
ROLE_ANTIGA=fugates_dev_app
SERVICO=fugates-callback-http
ACAO="${1:-conferir}"

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }

estado() {
  echo "-- runtime.env:"
  grep -oE 'Port=[0-9]+|Username=[a-z_]+|Database=[a-z_]+' "$ENV_FILE" | sort -u | sed 's/^/     /'
  echo "-- pgpass do servico (sem segredo):"
  cut -d: -f1-4 "$PGPASS_SVC" 2>/dev/null | sed 's/^/     /' || echo "     (ausente)"
  echo "-- servico:"
  echo "     ativo=$(systemctl is-active $SERVICO 2>/dev/null)"
  echo "     release em execucao=$(tr '\0' '\n' < /proc/"$(systemctl show -p MainPID --value $SERVICO)"/cmdline 2>/dev/null | grep -o 'releases/FugaTES.Callback.Http/[^/]*' | head -1 || echo '-')"
  echo "     listener 8080=$(ss -ltn | grep -c '127.0.0.1:8080')"
}

case "$ACAO" in
  conferir)
    echo "=== ESTADO ATUAL ==="; estado
    echo
    echo "Nada foi alterado."
    ;;

  aplicar)
    echo "=== ANTES ==="; estado; echo

    test -f "$PGPASS_ORIGEM" || { echo "BLOCKED: $PGPASS_ORIGEM ausente; a role de escrita nao foi provisionada"; exit 1; }
    LINHA=$(grep -E "^127\.0\.0\.1:5433:fugates_dev:${ROLE_NOVA}:" "$PGPASS_ORIGEM" || true)
    test -n "$LINHA" || { echo "BLOCKED: nao achei a linha de ${ROLE_NOVA} em $PGPASS_ORIGEM"; exit 1; }

    CARIMBO=$(date -u +%Y%m%d_%H%M%SZ)
    cp -p "$ENV_FILE" "$ENV_FILE.bak.r168.$CARIMBO"
    cp -p "$PGPASS_SVC" "$PGPASS_SVC.bak.r168.$CARIMBO" 2>/dev/null || true
    echo "backup: $ENV_FILE.bak.r168.$CARIMBO"

    # pgpass do servico: remove linha anterior da mesma role e acrescenta a nova, sem imprimir nada.
    DONO=$(stat -c %U:%G "$PGPASS_SVC" 2>/dev/null || echo fugates-http:fugates-http)
    TMP=$(mktemp); chmod 600 "$TMP"
    grep -vE "^127\.0\.0\.1:5433:fugates_dev:${ROLE_NOVA}:" "$PGPASS_SVC" 2>/dev/null > "$TMP" || true
    printf '%s\n' "$LINHA" >> "$TMP"
    install -o "${DONO%%:*}" -g "${DONO##*:}" -m 600 "$TMP" "$PGPASS_SVC"
    shred -u "$TMP" 2>/dev/null || rm -f "$TMP"
    unset LINHA

    sed -i "s/Port=5432/Port=5433/g; s/Username=${ROLE_ANTIGA}/Username=${ROLE_NOVA}/g" "$ENV_FILE"

    grep -q 'Port=5433' "$ENV_FILE" || { echo "BLOCKED: a porta nao foi alterada"; exit 1; }
    grep -q "Username=${ROLE_NOVA}" "$ENV_FILE" || { echo "BLOCKED: o usuario nao foi alterado"; exit 1; }

    systemctl restart "$SERVICO"
    sleep 4

    echo; echo "=== DEPOIS ==="; estado; echo
    if [ "$(systemctl is-active $SERVICO)" != active ]; then
      echo "BLOCKED: o servico nao subiu. Reverta com: sudo bash $0 reverter"
      journalctl -u "$SERVICO" -n 20 --no-pager | tail -20
      exit 1
    fi
    if [ "$(ss -ltn | grep -c '127.0.0.1:8080')" = 0 ]; then
      echo "BLOCKED: o servico subiu mas nao escuta em 8080. Reverta com: sudo bash $0 reverter"
      exit 1
    fi
    echo "PASS: recebimento SAP apontado para fugates_dev:5433 (cluster unificado)."
    echo "O proximo contrato do SAP deve aparecer no FugaTES DEV110."
    ;;

  reverter)
    ENV_BAK=$(ls -1t "$ENV_FILE".bak.r168.* 2>/dev/null | head -1)
    test -n "$ENV_BAK" || { echo "BLOCKED: nenhum backup r168 encontrado"; exit 1; }
    cp -p "$ENV_BAK" "$ENV_FILE"
    PG_BAK=$(ls -1t "$PGPASS_SVC".bak.r168.* 2>/dev/null | head -1)
    if [ -n "$PG_BAK" ]; then cp -p "$PG_BAK" "$PGPASS_SVC"; fi
    systemctl restart "$SERVICO"
    sleep 4
    echo "=== APOS REVERSAO ==="; estado
    echo "Revertido para $ENV_BAK"
    ;;

  *)
    echo "uso: sudo bash $0 [conferir|aplicar|reverter]"; exit 1 ;;
esac
