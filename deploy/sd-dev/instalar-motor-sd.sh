#!/usr/bin/env bash
# Publica o MOTOR de protocolos SD como servico proprio na VM.
#
# O que ele faz: a cada 3 minutos varre as ordens de venda no SAP e cria no Guardian o protocolo
# das linhas que ainda nao tem. Sem tela aberta e sem ninguem logado.
#
# Servico PROPRIO, e nao mais um drop-in no fugates-toledo-callback, por dois motivos aprendidos
# em 01/11/2026: aquele servico ja e disputado por drop-ins de varios autores (a corrida dos
# "zzzz-"), e um motor que cria protocolo real nao deve compartilhar processo com quem recebe
# POST da internet.
#
# As credenciais NAO sao criadas aqui e nao passam por este script. Ele as REUSA do servico que
# ja existe, copiando as linhas LoadCredential= do fugates-toledo-callback.service. Se elas nao
# estiverem la, o script para e diz o que falta - ele nunca inventa caminho de segredo.
#
# Uso:  sudo ./instalar-motor-sd.sh {conferir|ensaio|instalar|parar|reverter}
set -euo pipefail

SERVICO=fugates-sd-protocolo
# De quem herdamos as credenciais. Mais de um porque elas podem estar repartidas: o receptor da
# Toledo carrega as do SAP, e as do Guardian podem estar no carrier. Procuramos em ordem.
ORIGENS="fugates-toledo-callback fugates-carrier-sync"
ORIGEM_CRED=fugates-toledo-callback          # de onde herdamos usuario e grupo
PACOTE=${PACOTE:-/home/michael/fugates-motor-sd.tgz}
RAIZ=/opt/fugates/releases/FugaTES.HostIntegracao
RELEASE=MOTOR_SD_$(date -u +%Y%m%d_%H%M%SZ)
UNIDADE=/etc/systemd/system/$SERVICO.service
ENVFILE=/etc/fugates/sd-protocolo.env

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

# --- o que herdamos do servico que ja roda -------------------------------------------------

# "systemctl show -p LoadCredential" imprime "[unprintable]" - a propriedade nao sai por ali.
# A fonte e a propria unidade, com os seus drop-ins, que e o que "systemctl cat" compoe.
# LoadCredential ACUMULA entre drop-ins, e uma atribuicao vazia zera a lista: por isso o awk
# nao concatena cegamente.
entradas_de() {
  systemctl cat "$1" 2>/dev/null \
    | sed -n 's/^[[:space:]]*LoadCredential=[[:space:]]*//p' \
    | awk '{ if ($0 == "") { n = 0 } else { itens[++n] = $0 } }
            END { for (i = 1; i <= n; i++) print itens[i] }'
}

# Diretorio para as credenciais que NAO existem em nenhum servico da VM - hoje, as do Guardian,
# que ate agora so viviam no Gerenciador de Credenciais do Windows. Quem escreve os valores e o
# operador; este script apenas confere que estao la e aponta a unidade para eles.
PROPRIAS=/etc/fugates/sd-guardian

# Onde cada credencial exigida mora hoje. Devolve a entrada COMPLETA (nome:caminho), porque e o
# caminho que permite a outro servico carregar a mesma credencial.
procurar() {
  local nome="$1" origem entrada
  for origem in $ORIGENS; do
    entrada=$(entradas_de "$origem" | grep -m1 -E "^$nome:" || true)
    [ -n "$entrada" ] && { echo "$entrada"; return 0; }
  done
  [ -f "$PROPRIAS/$nome" ] && { echo "$nome:$PROPRIAS/$nome"; return 0; }
  return 1
}

# Credencial sem conteudo util sobe um servico que falha a cada volta. Conferir o TAMANHO e a
# permissao nao expoe o valor e evita descobrir isso so no primeiro POST ao Guardian.
conferir_arquivo_proprio() {
  # Duas linhas de proposito: o bash expande TODOS os argumentos de "local" antes de atribuir
  # qualquer um, entao "$nome" na mesma linha ainda nao existiria - e com set -u aborta o script.
  local nome="$1"
  local caminho="$PROPRIAS/$nome"
  [ -f "$caminho" ] || return 0
  [ -s "$caminho" ] || { echo "   AVISO: $nome esta vazio."; return 1; }
  if [ -n "$(tail -c 1 "$caminho")" ]; then :; else
    # O leitor usa o conteudo como veio, sem aparar: um \n no fim vira parte da senha.
    echo "   AVISO: $nome termina em quebra de linha - ela entraria no valor."
    return 1
  fi
  return 0
}

NECESSARIAS="carrier-hml-sap-user carrier-hml-sap-password
carrier-hml-guardian-email carrier-hml-guardian-password carrier-hml-guardian-signature"

credenciais() {
  local nome
  for nome in $NECESSARIAS; do procurar "$nome" || true; done
}

usuario_origem() { systemctl show "$ORIGEM_CRED" -p User --value; }
grupo_origem()   { systemctl show "$ORIGEM_CRED" -p Group --value; }

conferir_heranca() {
  local faltando=0 c
  for c in $NECESSARIAS; do
    procurar "$c" >/dev/null || { echo "FALTA credencial: $c"; faltando=1; }
  done
  [ "$faltando" = 0 ] || {
    echo
    echo "Nenhum dos servicos ($ORIGENS) expoe todas as credenciais necessarias,"
    echo "ou alguma delas e carregada sem caminho (LoadCredential=nome, sem ':')."
    echo "Nesse caso ela so existe dentro daquele servico e nao da para herdar."
    echo "Sem elas o motor nao sobe. Nada foi alterado."
    exit 1
  }
}

# --- modos -----------------------------------------------------------------------------------

case "${1:-}" in
conferir)
  echo "== Servico de origem das credenciais: $ORIGEM_CRED"
  systemctl is-active "$ORIGEM_CRED" >/dev/null 2>&1 && echo "   ativo" || echo "   NAO ativo"
  echo "== Usuario/grupo herdados: $(usuario_origem)/$(grupo_origem)"
  echo "== Credenciais exigidas pelo motor, e onde cada uma esta hoje:"
  echo "   (apenas NOMES e CAMINHOS; nenhum valor e lido)"
  # Sem "&&" como ultimo comando de laco aqui: um laco que termina com status diferente de zero
  # encerra o script por causa do set -e, e a lista sairia pela metade sem dizer por que.
  for c in $NECESSARIAS; do
    achada=$(procurar "$c" || true)
    if [ -z "$achada" ]; then
      echo "   $c   <- NAO ENCONTRADA (esperada em $PROPRIAS/$c)"
    else
      de="arquivo local"
      for o in $ORIGENS; do
        if entradas_de "$o" | grep -qE "^$c:"; then de="$o"; break; fi
      done
      echo "   $achada   <- $de"
    fi
    conferir_arquivo_proprio "$c" || true
  done
  echo "== Entradas LoadCredential vistas em cada servico:"
  for o in $ORIGENS; do
    echo "   [$o]"
    entradas_de "$o" | sed 's/^/      /' || true
  done
  echo "== Motor ja instalado? $([ -f "$UNIDADE" ] && echo sim || echo nao)"
  [ -f "$UNIDADE" ] && { echo "== Estado:"; systemctl is-active "$SERVICO" || true; }
  echo "== Pacote esperado em: $PACOTE  $([ -f "$PACOTE" ] && echo '(presente)' || echo '(AUSENTE)')"
  ;;

ensaio)
  # Monta tudo num diretorio temporario e NAO toca no systemd. Serve para ver a unidade que
  # seria escrita, com as credenciais que seriam herdadas, antes de instalar de verdade.
  conferir_heranca
  TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
  {
    echo "[Unit]"
    echo "Description=FugaTES - motor de protocolos SD (varre SAP, cria protocolo no Guardian)"
    echo "After=network-online.target postgresql.service"
    echo
    echo "[Service]"
    echo "Type=notify"
    echo "User=$(usuario_origem)"
    echo "Group=$(grupo_origem)"
    credenciais | sed 's/^/LoadCredential=/'
    echo "EnvironmentFile=$ENVFILE"
    echo "WorkingDirectory=$RAIZ/$RELEASE"
    echo "ExecStart=$RAIZ/$RELEASE/FugaTES.HostIntegracao"
    echo "Restart=on-failure"
    echo "RestartSec=30"
    echo "NoNewPrivileges=true"
    echo "ProtectSystem=strict"
    echo "ProtectHome=true"
    echo "PrivateTmp=true"
    echo
    echo "[Install]"
    echo "WantedBy=multi-user.target"
  } > "$TMP/unidade"
  echo "== Unidade que seria escrita em $UNIDADE:"
  sed 's/^/   /' "$TMP/unidade"
  echo
  echo "== Arquivo de ambiente que seria escrito em $ENVFILE:"
  echo "   (sem segredo: so recorte e endereco)"
  sed 's/^/   /' <<EOF
HostIntegracao__Mode=SdProtocolo
Sap__BaseUrl=https://vhfufds4ci.sap.fugacouros.com.br:44300
Sap__SapClient=110
Guardian__BaseUrl=https://guardian.cloudprix.com.br
SdProtocolo__Enabled=true
SdProtocolo__Intervalo=00:03:00
SdProtocolo__CorteCriacao=2026-11-01
SdProtocolo__ProtocolosPorVarredura=10
SdProtocolo__OrdensPorVarredura=200
SdProtocolo__UrlParaRetorno=<PREENCHER>
SdProtocolo__ConnectionString=<PREENCHER>
EOF
  echo
  echo "== Nada foi instalado. Ensaio apenas."
  ;;

instalar)
  conferir_heranca
  [ -f "$PACOTE" ] || { echo "Pacote ausente em $PACOTE. Nada foi feito."; exit 1; }
  [ -f "$ENVFILE" ] || {
    echo "Falta $ENVFILE."
    echo "Crie-o primeiro (veja 'ensaio' para o conteudo) e preencha UrlParaRetorno e"
    echo "ConnectionString. Este script nao escreve esse arquivo: a cadeia de conexao e sua."
    exit 1
  }
  for chave in SdProtocolo__UrlParaRetorno SdProtocolo__ConnectionString; do
    grep -q "^$chave=" "$ENVFILE" || { echo "Falta $chave em $ENVFILE."; exit 1; }
    grep -q "^$chave=<PREENCHER>" "$ENVFILE" && { echo "$chave ainda esta como <PREENCHER>."; exit 1; }
  done

  install -d -m 0755 "$RAIZ"
  install -d -m 0755 "$RAIZ/$RELEASE"
  tar -xzf "$PACOTE" -C "$RAIZ/$RELEASE"
  chmod +x "$RAIZ/$RELEASE/FugaTES.HostIntegracao"
  chown -R "$(usuario_origem)":"$(grupo_origem)" "$RAIZ/$RELEASE"
  echo "== Release em $RAIZ/$RELEASE"

  {
    echo "[Unit]"
    echo "Description=FugaTES - motor de protocolos SD (varre SAP, cria protocolo no Guardian)"
    echo "After=network-online.target postgresql.service"
    echo
    echo "[Service]"
    echo "Type=notify"
    echo "User=$(usuario_origem)"
    echo "Group=$(grupo_origem)"
    credenciais | sed 's/^/LoadCredential=/'
    echo "EnvironmentFile=$ENVFILE"
    echo "WorkingDirectory=$RAIZ/$RELEASE"
    echo "ExecStart=$RAIZ/$RELEASE/FugaTES.HostIntegracao"
    echo "Restart=on-failure"
    echo "RestartSec=30"
    echo "NoNewPrivileges=true"
    echo "ProtectSystem=strict"
    echo "ProtectHome=true"
    echo "PrivateTmp=true"
    echo
    echo "[Install]"
    echo "WantedBy=multi-user.target"
  } > "$UNIDADE"
  chmod 0644 "$UNIDADE"

  systemctl daemon-reload
  systemctl enable "$SERVICO" >/dev/null
  systemctl restart "$SERVICO"
  sleep 5

  if ! systemctl is-active --quiet "$SERVICO"; then
    echo "== FALHOU ao subir. Ultimas linhas do journal:"
    journalctl -u "$SERVICO" -n 30 --no-pager
    exit 1
  fi
  echo "== Ativo. O motor registra cada volta no journal:"
  echo "   journalctl -u $SERVICO -f"
  journalctl -u "$SERVICO" -n 15 --no-pager
  ;;

parar)
  # Desliga o motor sem desinstalar: util para pausar a criacao sem perder a configuracao.
  systemctl stop "$SERVICO"
  echo "== Parado. A configuracao e o release continuam no lugar."
  echo "   Para religar: sudo systemctl start $SERVICO"
  ;;

reverter)
  echo "ATENCAO: remove o servico do motor SD. Os protocolos ja criados no Guardian permanecem."
  printf 'Digite REVERTER para confirmar: '; read -r r
  [ "$r" = "REVERTER" ] || { echo "Cancelado."; exit 1; }
  systemctl disable --now "$SERVICO" 2>/dev/null || true
  rm -f "$UNIDADE"
  systemctl daemon-reload
  echo "== Removido. $ENVFILE e os releases em $RAIZ foram mantidos."
  ;;

*)
  echo "Uso: sudo $0 {conferir|ensaio|instalar|parar|reverter}"
  exit 1
  ;;
esac
