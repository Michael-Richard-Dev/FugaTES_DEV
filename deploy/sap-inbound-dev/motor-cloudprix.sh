#!/usr/bin/env bash
# R169 - Motor de integracao FugaTES -> CloudPrix (Guardian), para o DEV unificado.
#
# CONTEXTO
#   O receptor SAP ja grava em fugates_dev:5433. Quem envia o contrato ao CloudPrix e o MOTOR,
#   um processo separado (FugaTES.HostIntegracao) que hoje so existe em modo inerte (Fake/Fake).
#   Este script instala uma unidade NOVA, parada, sem tocar nos servicos existentes.
#
# POR QUE ESCOPO PILOTO, E NAO JANELA R57
#   O escopo de janela exige que o correlation_id gravado comece com o prefixo da janela, e o
#   receptor so carimba esse prefixo se ele proprio tiver janela - o que exigiria
#   SecurityMode=LoopbackBasicHttps e, portanto, X-Forwarded-Proto=https, que o ingresso NAO envia.
#   Logo, uma janela no motor nunca casaria com o que o receptor grava.
#   O escopo PILOTO nao tem essa amarracao e e mais restrito: autoriza UM numero de contrato.
#   Com ele o motor tambem nao recupera leases antigas nem ingere backlog (ver MotorIntegracaoServico).
#
# O QUE E REAPROVEITADO, NADA E CRIADO DO ZERO
#   binario      : /opt/fugates/releases/FugaTES.ACM/R165Y_PARTNER_20261025_R3/runtime/host
#   credenciais  : /etc/fugates/acm-r165y/guardian-rest-hml.env  (Guardian__BaseUrl/Email/Password/
#                  Signature/PreCadastroEndpoint/Timeout) - carregadas pelo systemd, nunca lidas aqui
#   banco        : fugates_acm_hml_app, que tem UPDATE em integracao.estado e INSERT em
#                  auditoria_integracao e tentativa_integracao - privilegios que a role do receptor
#                  nao tem de proposito
#
# ATENCAO
#   Guardian__BaseUrl aponta para https://guardian.cloudprix.com.br - o host REAL do CloudPrix.
#   Ligar este motor faz cadastro de verdade. Por isso ele e instalado PARADO e exige o numero do
#   contrato para ligar.
#
# USO (na VM):
#   sudo bash motor-cloudprix.sh instalar
#   sudo bash motor-cloudprix.sh estado
#   sudo bash motor-cloudprix.sh ligar <NUMERO_CONTRATO> <ITEM>
#   sudo bash motor-cloudprix.sh desligar

set -euo pipefail

UNIT=/etc/systemd/system/fugates-acm-motor-dev.service
ENVDIR=/etc/fugates/motor-dev
ENVBASE=$ENVDIR/motor.env
ENVESCOPO=$ENVDIR/escopo.env
GUARDIAN_ENV=/etc/fugates/acm-r165y/guardian-rest-hml.env
RELEASE=/opt/fugates/releases/FugaTES.ACM/R165Y_PARTNER_20261025_R3/runtime/host
PGPASS=/home/michael/.fugates-acm-r165p/pgpass
SERVICO=fugates-acm-motor-dev
ACAO="${1:-estado}"

test "$(id -u)" = 0 || { echo "BLOCKED: execute com sudo"; exit 1; }

preflight() {
  local ok=0
  echo "-- preflight:"
  test -f "$RELEASE/FugaTES.HostIntegracao.dll" && echo "   binario do motor        OK" || { echo "   binario do motor        FALTA ($RELEASE)"; ok=1; }
  test -f "$GUARDIAN_ENV"                        && echo "   credenciais CloudPrix   OK" || { echo "   credenciais CloudPrix   FALTA ($GUARDIAN_ENV)"; ok=1; }
  test -f "$PGPASS"                              && echo "   pgpass do banco         OK" || { echo "   pgpass do banco         FALTA ($PGPASS)"; ok=1; }
  if sudo -u michael env PGPASSFILE=$PGPASS psql -w \
       'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc 'select 1' >/dev/null 2>&1
  then echo "   banco 5433              OK"; else echo "   banco 5433              FALHOU"; ok=1; fi
  echo "   receptor SAP -> 5433    $(grep -q 'Port=5433' /etc/fugates/callback-http/runtime.env 2>/dev/null && echo OK || echo 'NAO (o contrato nao chegara ao banco unificado)')"
  return $ok
}

case "$ACAO" in
  instalar)
    preflight || { echo; echo "BLOCKED: corrija os itens acima antes de instalar."; exit 1; }

    install -d -m 750 -o root -g michael "$ENVDIR"

    cat > "$ENVBASE" <<EOF
# R169 - configuracao do motor DEV. Sem segredo: a credencial do CloudPrix vem do
# EnvironmentFile proprio, carregado pelo systemd.
ConnectionStrings__FugaTES=Host=127.0.0.1;Port=5433;Database=fugates_dev;Username=fugates_acm_hml_app;Search Path=fugates;Passfile=$PGPASS;Enlist=false
HostIntegracao__PersistenceMode=PostgreSql
HostIntegracao__ExternalMode=Guardian
HostIntegracao__PollInterval=00:00:05
HostIntegracao__ShutdownGracePeriod=00:00:30
TimeoutProcessamentoIntegracao__Timeout=00:01:00
MotorIntegracao__LeaseDuration=00:03:00
MotorIntegracao__LeaseRenewInterval=00:00:20
MotorIntegracao__RetryBackoff=1.00:00:00
# Diagnostico: Debug para revelar a razao da recusa. Volte para Warning depois do primeiro sucesso.
Logging__LogLevel__Default=Debug
Logging__LogLevel__Microsoft=Warning
Logging__LogLevel__System=Warning
MotorIntegracao__AcmInboundWindow__Enabled=false
# Cadastro de contrato no Guardian: e um servico SOAP INTERNO, nao a REST da nuvem.
# O proprio produto so aprova HTTP neste perfil para host 192.168.17.91 porta 80 em ambiente HML
# (ValidateContratoSoapEndpoint). Endpoint validado em 29/10 por POST manual: CadastraContrato
# respondeu Erro=0, "Contrato cadastrado com sucesso!".
Guardian__ContratoSoapEndpoint=http://192.168.17.91:80/WS_GUARDIAN/WS_GUARDIAN_PLUS.asmx
Guardian__ContratoSoapEnvironment=HML
Guardian__ContratoSoapInternalHmlHttpApproved=true
AcmR57__Enabled=true
AcmR57__Normal__Enabled=true
AcmR57__Normal__ExecutionAuthorized=true
AcmR57__Normal__GuardianEnvironment=HML
AcmR57__Normal__CallbackApproved=false
AcmR57__Normal__IdAgenda=1
Sap__Enabled=false
Callback__Enabled=false
SapContractInbound__ProductiveEnabled=false
SapContractInbound__OfflineEnabled=false
EOF
    chown root:michael "$ENVBASE"; chmod 640 "$ENVBASE"

    # Escopo piloto: vazio ate o 'ligar' informar o contrato. Sem ele o motor nao sobe.
    printf '# preenchido por "ligar"\n' > "$ENVESCOPO"
    chown root:michael "$ENVESCOPO"; chmod 640 "$ENVESCOPO"

    cat > "$UNIT" <<EOF
[Unit]
Description=FugaTES ACM - motor DEV unificado (FugaTES -> CloudPrix)
After=network-online.target postgresql.service
Wants=network-online.target

[Service]
Type=simple
User=michael
Group=michael
WorkingDirectory=$RELEASE
EnvironmentFile=$GUARDIAN_ENV
EnvironmentFile=$ENVBASE
EnvironmentFile=$ENVESCOPO
Environment=DOTNET_EnableDiagnostics=0
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=FUGATES_INTEGRATION_WORKER_ENABLED=true
# TRAVA DE SEGURANCA: sem contrato no escopo piloto, UsaEscopoAcm fica falso e o motor
# despacharia QUALQUER integracao pendente ao CloudPrix. O ExecStartPre impede a partida
# nesse caso, inclusive se alguem der "systemctl start" direto, sem passar pelo script.
ExecStartPre=/bin/sh -c 'grep -qE "^MotorIntegracao__AcmPilotNumeroContrato=[0-9]+|^MOTOR_MODO_AUTOMATICO=1" $ENVESCOPO'
ExecStart=/usr/bin/dotnet $RELEASE/FugaTES.HostIntegracao.dll
Restart=no
TimeoutStopSec=45
KillMode=control-group
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=read-only
ReadOnlyPaths=$PGPASS
UMask=0077
SyslogIdentifier=fugates-acm-motor-dev

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload
    systemctl disable "$SERVICO" >/dev/null 2>&1 || true
    echo
    echo "PASS: motor instalado e PARADO."
    echo "  unidade : $UNIT"
    echo "  config  : $ENVBASE"
    echo "  ligar   : sudo bash $0 ligar <NUMERO_CONTRATO> <ITEM>"
    ;;

  ligar)
    CONTRATO="${2:-}"; ITEM="${3:-}"
    test -n "$CONTRATO" || { echo "BLOCKED: informe o numero do contrato. Ex: sudo bash $0 ligar 0000001161 10"; exit 1; }
    test -n "$ITEM"     || { echo "BLOCKED: informe o item, ou \"-\" se o contrato nao tiver item. Ex: ligar 1160 -"; exit 1; }
    echo "$CONTRATO" | grep -qE '^[0-9]{1,20}$' || { echo "BLOCKED: numero de contrato invalido (so digitos)"; exit 1; }
    test -f "$UNIT" || { echo "BLOCKED: motor nao instalado. Rode: sudo bash $0 instalar"; exit 1; }

    # O contrato precisa ESTAR no banco: o motor nao cria, ele envia o que o receptor gravou.
    N=$(sudo -u michael env PGPASSFILE=$PGPASS psql -w \
        'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc \
        "select count(*) from fugates.contrato_sap_toledo_versao where ltrim(numero_contrato,'0')=ltrim('$CONTRATO','0')" 2>/dev/null || echo 0)
    if [ "${N:-0}" = 0 ]; then
      echo "BLOCKED: o contrato $CONTRATO nao esta em fugates_dev:5433."
      echo "         O SAP precisa envia-lo primeiro; o motor so despacha o que ja foi recebido."
      exit 1
    fi

    # O escopo do motor compara chave_negocio por igualdade EXATA de string. O que o operador digita
    # pode vir sem os zeros a esquerda, entao resolvemos a forma canonica direto do banco.
    CANON=$(sudo -u michael env PGPASSFILE=$PGPASS psql -w "host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app" -tAc "select chave_negocio from fugates.integracao where ltrim(chave_negocio,'0')=ltrim('$CONTRATO','0') order by criado_em desc limit 1" 2>/dev/null || true)
    if [ -n "$CANON" ] && [ "$CANON" != "$CONTRATO" ]; then
      echo "  numero normalizado: $CONTRATO -> $CANON (forma gravada no banco)"
      CONTRATO="$CANON"
    fi

    # AcmPilotItem pode ser NULL, mas nao string vazia (EhValida). Quando o contrato nao traz Item,
    # a variavel tem de ser OMITIDA: definida vazia, viraria "" e invalidaria o escopo.
    {
      printf "MotorIntegracao__AcmPilotNumeroContrato=%s\n" "$CONTRATO"
      printf "MotorIntegracao__AcmPilotCentro=3011\n"
      if [ "$ITEM" != "-" ]; then printf "MotorIntegracao__AcmPilotItem=%s\n" "$ITEM"; fi
    } > "$ENVESCOPO"
    chown root:michael "$ENVESCOPO"; chmod 640 "$ENVESCOPO"

    systemctl daemon-reload
    systemctl start "$SERVICO"
    sleep 5
    echo "-- estado:"; systemctl is-active "$SERVICO"
    if [ "$(systemctl is-active $SERVICO)" != active ]; then
      echo "BLOCKED: o motor nao subiu. Ultimas linhas:"
      journalctl -u "$SERVICO" -n 25 --no-pager
      exit 1
    fi
    echo
    echo "MOTOR LIGADO para o contrato $CONTRATO / item $ITEM."
    echo "Acompanhe:  sudo journalctl -u $SERVICO -f"
    echo "Desligue:   sudo bash $0 desligar"
    ;;

  automatico)
    # Modo AUTOMATICO: sem escopo piloto. O motor despacha ao CloudPrix TODA integracao que entrar
    # na fila, sem nomear contrato. E o que o operador pediu para o fluxo fim a fim - e por isso
    # exige confirmacao explicita e fila limpa na partida.
    test -f "$UNIT" || { echo "BLOCKED: motor nao instalado. Rode: sudo bash $0 instalar"; exit 1; }
    if [ "${2:-}" != "--confirmo" ]; then
      echo "O modo automatico despacha ao CloudPrix QUALQUER contrato que chegar, sem conferencia."
      echo "Repita com: sudo bash $0 automatico --confirmo"
      exit 1
    fi

    # Fila tem que estar limpa: nada de despachar sobra de teste ou backlog antigo sem querer.
    PEND=$(sudo -u michael env PGPASSFILE=$PGPASS psql -w \
      'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tAc \
      "select count(*) from fugates.integracao where estado in ('PENDENTE','AGUARDANDO_REPROCESSAMENTO')" 2>/dev/null || echo erro)
    if [ "$PEND" != 0 ]; then
      echo "BLOCKED: ha $PEND integracao(oes) pendente(s) na fila."
      echo "         No automatico elas seriam despachadas ao CloudPrix imediatamente."
      echo "         Revise antes, ou use o modo piloto: sudo bash $0 ligar <contrato> <item>"
      exit 1
    fi

    printf 'MOTOR_MODO_AUTOMATICO=1\n' > "$ENVESCOPO"
    chown root:michael "$ENVESCOPO"; chmod 640 "$ENVESCOPO"
    systemctl daemon-reload
    systemctl start "$SERVICO"
    sleep 5
    if [ "$(systemctl is-active $SERVICO)" != active ]; then
      echo "BLOCKED: o motor nao subiu."; journalctl -u "$SERVICO" -n 25 --no-pager; exit 1
    fi
    echo
    echo "MOTOR EM MODO AUTOMATICO. Fila estava limpa na partida."
    echo "Todo contrato que o receptor gravar sera enviado ao CloudPrix (https://guardian.cloudprix.com.br)."
    echo "Acompanhe:  sudo journalctl -u $SERVICO -f"
    echo "Pare:       sudo bash $0 desligar"
    ;;

  desligar)
    systemctl stop "$SERVICO" 2>/dev/null || true
    printf '# desligado\n' > "$ENVESCOPO"; chown root:michael "$ENVESCOPO"; chmod 640 "$ENVESCOPO"
    systemctl daemon-reload
    echo "Motor parado e escopo limpo. Nada mais sera enviado ao CloudPrix."
    ;;

  estado)
    echo "=== MOTOR DEV (FugaTES -> CloudPrix) ==="
    test -f "$UNIT" && echo "instalado  : sim" || echo "instalado  : nao"
    echo "ativo      : $(systemctl is-active $SERVICO 2>/dev/null || echo inexistente)"
    echo "escopo     : $(grep -s AcmPilotNumeroContrato "$ENVESCOPO" | cut -d= -f2 || echo '(vazio)')"
    echo
    preflight || true
    ;;

  *) echo "uso: sudo bash $0 [instalar|estado|automatico --confirmo|ligar <contrato> <item>|desligar]"; exit 1 ;;
esac
