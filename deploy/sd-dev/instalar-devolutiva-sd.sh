#!/usr/bin/env bash
# Publica o receptor da devolutiva SD na instancia que ja atende o Guardian.
#
# Descoberto em 01/11/2026, e o motivo de este script existir em vez de um generico:
#   - a rota do Guardian vai para 127.0.0.1:18086, que e o fugates-toledo-callback,
#     e NAO para a 8080 (fugates-callback-http);
#   - o nginx usa "location =" (correspondencia exata), entao /callback/venda nao chega
#     ao aplicativo enquanto nao existir um bloco proprio.
#
# Por isso sao duas mudancas, nesta ordem: aplicativo primeiro, porta de entrada depois.
# Se a primeira falhar, a segunda nao acontece e nada e exposto.
#
# Uso:  sudo ./instalar-devolutiva-sd.sh {ensaio|conferir|instalar|reverter}
set -euo pipefail

SERVICO=fugates-toledo-callback
PORTA=18086
ROTA=/api/guardian/set26/callback/venda
ROTA_ACM=/api/guardian/set26/callback/recebimento
PACOTE=${PACOTE:-/home/michael/fugates-sd-devolutiva.tgz}
SHA_ESPERADO=3a170ea3400726763637d247eb720ca74610b1c9f62a7dd15d18f3d1e51655bc
RAIZ=/opt/fugates/releases/FugaTES.Callback.Http
RELEASE=SD_DEVOLUTIVA_$(date -u +%Y%m%d_%H%M%SZ)
# O nome precisa ordenar DEPOIS de todos os drop-ins existentes: ha onze deles, de 90 a 99, e
# cada um redefine o ExecStart. Um "90-" seria sobrescrito pelos seguintes, e o servico subiria
# com o release antigo - foi o que aconteceu nas duas primeiras tentativas. "zz-" vem depois de
# qualquer prefixo numerico.
# ... e depois apareceu um "zzzzzzzzzzzz-TOLEDO_NAME_LIMITS" (doze zes), publicado em paralelo,
# que vencia o "zz-". Isto aqui e uma corrida de nomes, nao uma solucao: quem publicar por ultimo
# com mais zes vence. A solucao de verdade e combinar quem publica neste servico - enquanto nao
# houver essa convencao, a conferencia de ExecStart logo abaixo e o que impede o engano silencioso.
DROPIN=/etc/systemd/system/$SERVICO.service.d/zzzzzzzzzzzzz-sd-devolutiva.conf
# Fora de /etc/fugates/callback-http: aquele diretorio e do OUTRO servico (porta 8080), e
# misturar arquivos de ambiente entre os dois convida exatamente o engano que ja tivemos.
ENVFILE=/etc/fugates/sd-devolutiva.env

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

atual() { systemctl show "$SERVICO" -p ExecStart --value | grep -oE "$RAIZ/[^/]+" | head -1; }
# Linha de comando INTEIRA do servico, com argumentos. Derivar dela em vez de reescrever o
# ExecStart e o que impede perder parametros: foi assim que o "--urls http://127.0.0.1:18086"
# sumiu e o processo tentou subir na 8080, que e do outro servico.
argv_atual() { systemctl show "$SERVICO" -p ExecStart --value | sed -n 's/.*argv\[\]=\(.*\) ; ignore_errors.*/\1/p' | head -1; }
usuario() { systemctl show "$SERVICO" -p User --value; }
# O arquivo que o nginx REALMENTE le, e nao o primeiro do disco que contenha a rota: havia uma
# copia ".backup" com o mesmo conteudo, e editar ela passou no "nginx -t" sem efeito nenhum.
# "nginx -T" imprime so os arquivos incluidos, cada um precedido de "# configuration file <path>:".
nginx_arquivo() {
  nginx -T 2>/dev/null | awk -v alvo="location = $ROTA_ACM" '
    /^# configuration file / { arquivo=$4; sub(/:$/,"",arquivo) }
    index($0,alvo) { print arquivo; exit }'
}

saude() {
  curl -s -m 5 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORTA/health" 2>/dev/null || echo 000
}
rota_app() {
  curl -s -m 5 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORTA$ROTA" 2>/dev/null || echo 000
}
rota_acm() {
  curl -s -m 5 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORTA$ROTA_ACM" 2>/dev/null || echo 000
}

case "${1:-}" in

# Sobe o release novo em porta propria, com o MESMO ambiente do servico, sem parar nada.
# Existe porque usar o servico real como teste interrompe o ACM a cada tentativa - e ja foram
# quatro. Aqui a falha aparece na tela, com a excecao, e o ACM nem percebe.
ensaio)
  [ -f "$PACOTE" ] || { echo "Pacote ausente: $PACOTE"; exit 1; }
  USUARIO=$(usuario); PORTA_ENSAIO=18099
  TMPDIR=$(mktemp -d /tmp/sd-ensaio-XXXX)
  tar -xzf "$PACOTE" -C "$TMPDIR"
  rm -f "$TMPDIR"/appsettings*.json
  chown -R "$USUARIO:$USUARIO" "$TMPDIR"

  # Mesma lista de EnvironmentFile que o systemd entrega ao servico, na mesma ordem.
  ARQUIVOS=$(systemctl show "$SERVICO" -p EnvironmentFiles --value | tr ' ' '\n' | sed 's/ (ignore_errors=.*//' | grep -v '^$')
  ENVARGS=()
  # A unidade tambem pode trazer diretivas Environment= alem dos arquivos. Ler so os arquivos
  # dava um ambiente incompleto, e a admissao do Toledo recusava com 403 por falta de opcao.
  while IFS= read -r par; do
    [ -n "$par" ] || continue
    printf '%s' "$par" | grep -qE '^[A-Za-z_][A-Za-z0-9_]*=' || continue
    ENVARGS+=("$par")
  done < <(systemctl show "$SERVICO" -p Environment --value | tr ' ' '\n')
  while read -r f; do
    f=${f%%:*}
    [ -f "$f" ] || continue
    while IFS= read -r linha; do
      case "$linha" in ''|\#*) continue;; esac
      # Mesma regra do systemd: atribuicao com nome valido, senao ignora.
      printf '%s' "$linha" | grep -qE '^[A-Za-z_][A-Za-z0-9_]*=' || continue
      # O systemd REMOVE aspas que envolvem o valor; o env nao remove. Sem isto, um
      # CHAVE="true" chega ao processo como "true" com aspas e nenhum parser o aceita -
      # criando uma falha que o servico real nao tem.
      nome=${linha%%=*}; valor=${linha#*=}
      case "$valor" in
        \"*\") valor=${valor#\"}; valor=${valor%\"} ;;
        \'*\') valor=${valor#\'}; valor=${valor%\'} ;;
      esac
      ENVARGS+=("$nome=$valor")
    done < "$f"
  done <<< "$ARQUIVOS"
  ENVARGS+=("SdInbound__DevolutivaEnabled=true")

  # Usa a MESMA linha de comando do servico, trocando so o caminho e a porta. Antes eu passava
  # ASPNETCORE_URLS por ambiente, o que mascarava qualquer erro nos argumentos - e mascarou: o
  # "--urls" sumido so apareceu no servico real, depois de o ACM cair.
  ARGV_ENSAIO=$(argv_atual)
  ARGV_ENSAIO=$(printf '%s' "$ARGV_ENSAIO" | sed "s|$RAIZ/[^/ ]*|$TMPDIR|; s|:$PORTA\b|:$PORTA_ENSAIO|")
  [ -n "$ARGV_ENSAIO" ] || { echo "Nao consegui derivar a linha de comando."; rm -rf "$TMPDIR"; exit 1; }
  echo "ensaio em 127.0.0.1:$PORTA_ENSAIO  (servico $SERVICO segue intocado)"
  echo "comando: $ARGV_ENSAIO"
  SAIDA=$(mktemp)
  sudo -u "$USUARIO" env "${ENVARGS[@]}" $ARGV_ENSAIO > "$SAIDA" 2>&1 &
  PID=$!
  for _ in $(seq 1 25); do
    kill -0 $PID 2>/dev/null || break
    [ "$(curl -s -m 2 -o /dev/null -w '%{http_code}' http://127.0.0.1:$PORTA_ENSAIO/health 2>/dev/null)" = 200 ] && break
    sleep 1
  done
  H=$(curl -s -m 3 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORTA_ENSAIO/health" 2>/dev/null || echo 000)
  A=$(curl -s -m 3 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORTA_ENSAIO$ROTA_ACM" 2>/dev/null || echo 000)
  S=$(curl -s -m 3 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORTA_ENSAIO$ROTA" 2>/dev/null || echo 000)
  # A rota do ACM exige porta local 18086 na admissao (TOLEDO_SOURCE_NOT_ALLOWED). No ensaio,
  # que roda em outra porta para nao disputar com o servico, 403 e o resultado CORRETO.
  echo "  saude=$H  rotaSD=$S   (esperado 200 / 200)"
  echo "  rotaACM=$A   (403 e esperado aqui: a admissao exige porta 18086)"
  if [ "$H" != 200 ] || [ "$S" != 200 ]; then
    echo "=== saida do processo (ultimas 30 linhas):"
    grep -vE '^\s*at |^info:|^warn:' "$SAIDA" | tail -30
  fi
  kill $PID 2>/dev/null || true; sleep 1; kill -9 $PID 2>/dev/null || true
  rm -rf "$TMPDIR" "$SAIDA"
  ;;

# So a porta de entrada. Existe porque a instalacao do aplicativo e do nginx sao etapas
# independentes: se a primeira ja passou, repetir tudo criaria um release a toa.
nginx)
  NGINX=$(nginx_arquivo)
  [ -n "$NGINX" ] || { echo "Nao achei a rota do ACM na configuracao efetiva do nginx."; exit 1; }
  echo "arquivo efetivo: $NGINX"
  case "$NGINX" in
    *.backup*|*.bak*|*~) echo "FALHOU: isso e uma copia, nao a configuracao ativa."; exit 1 ;;
  esac
  # Desfaz a edicao anterior em arquivo errado, se houve: deixar um bloco solto numa copia
  # confunde quem for ler depois.
  for copia in /etc/nginx/sites-available/*.backup* /etc/nginx/sites-available/*.bak*; do
    [ -f "$copia" ] || continue
    case "$copia" in *.bak-sd-*) continue ;; esac
    if grep -q "location = $ROTA" "$copia" 2>/dev/null; then
      ULT=$(ls -t "$copia".bak-sd-* 2>/dev/null | head -1 || true)
      [ -n "$ULT" ] && cp -p "$ULT" "$copia" && echo "revertida edicao indevida em $copia"
    fi
  done
  if [ "$(rota_app)" != 200 ]; then
    echo "FALHOU: a rota SD nao responde no aplicativo ($(rota_app))."
    echo "Publique o aplicativo antes: $0 instalar"; exit 1
  fi
  if grep -qE "location = $ROTA( |\{|$)" "$NGINX"; then
    echo "nginx: bloco da rota SD ja existe, nada a fazer."
  else
    rm -f "$NGINX.novo"
    cp -p "$NGINX" "$NGINX.bak-sd-$(date -u +%Y%m%d%H%M%SZ)"
    awk -v acm="location = $ROTA_ACM" -v caminho="$ROTA_ACM" -v nova="$ROTA" '
      $0 ~ acm && !feito { dentro=1; chaves=0 }
      # A linha ORIGINAL sai primeiro, e so entao a copia. Emitir a copia ao fechar as chaves
      # a colocava DENTRO do bloco do ACM, e o nginx recusa location aninhada em location exata.
      { print }
      dentro {
        linha=$0; sub(caminho, nova, linha); bloco = bloco linha "\n"
        chaves += gsub(/\{/,"{") - gsub(/\}/,"}")
        if (chaves==0) { printf "\n%s", bloco; bloco=""; dentro=0; feito=1 }
      }
    ' "$NGINX" > "$NGINX.novo"
    grep -q "location = $ROTA" "$NGINX.novo" || { echo "FALHOU: o bloco nao foi gerado."; rm -f "$NGINX.novo"; exit 1; }
    mv "$NGINX.novo" "$NGINX"
    if ! nginx -t 2>&1 | tail -2; then
      echo "FALHOU: nginx -t recusou. Restaurando."
      cp -p "$(ls -t "$NGINX".bak-sd-* | head -1)" "$NGINX"; nginx -t >/dev/null 2>&1; exit 1
    fi
    systemctl reload nginx
    echo "nginx: bloco da rota SD publicado e recarregado."
  fi
  # O reload do nginx e gracioso: os processos novos assumem em seguida, nao na hora. Medir
  # imediatamente devolvia 404 e parecia falha.
  sleep 3
  echo "=== conferencia final (de dentro da VM, origem 127.0.0.1):"
  printf '  porta 80, rota SD -> '; curl -s -m 5 -o /dev/null -w '%{http_code}\n' "http://127.0.0.1$ROTA"
  echo "  403 aqui e CORRETO: a lista do nginx so admite as origens autorizadas."
  printf '  porta 80, rota ACM -> '; curl -s -m 5 -o /dev/null -w '%{http_code}\n' "http://127.0.0.1$ROTA_ACM"
  ;;

conferir)
  echo "servico .......... $SERVICO ($(systemctl is-active $SERVICO))"
  echo "usuario .......... $(usuario)"
  echo "release atual .... $(atual)"
  echo "nginx ............ $(nginx_arquivo || echo 'NAO ENCONTRADO')"
  echo "saude  $PORTA ...... $(saude)   (esperado 200)"
  echo "rota ACM ......... $(rota_acm)   (200 = probe responde)"
  echo "rota SD .......... $(rota_app)   (404 antes de instalar)"
  echo "pacote ........... $([ -f "$PACOTE" ] && echo presente || echo AUSENTE)"
  [ -f "$PACOTE" ] && echo "sha confere ...... $([ "$(sha256sum "$PACOTE" | cut -d' ' -f1)" = "$SHA_ESPERADO" ] && echo SIM || echo NAO)"
  ;;

instalar)
  [ -f "$PACOTE" ] || { echo "Pacote ausente: $PACOTE"; exit 1; }
  [ "$(sha256sum "$PACOTE" | cut -d' ' -f1)" = "$SHA_ESPERADO" ] || { echo "SHA do pacote nao confere."; exit 1; }
  ANTERIOR=$(atual); USUARIO=$(usuario); NGINX=$(nginx_arquivo); ARGV_ANTIGO=$(argv_atual)
  [ -n "$ANTERIOR" ] || { echo "Nao identifiquei o release atual."; exit 1; }
  [ -n "$NGINX" ] || { echo "Nao achei o arquivo do nginx com a rota do ACM."; exit 1; }
  echo "release anterior: $ANTERIOR"
  echo "release novo ...: $RAIZ/$RELEASE"
  echo "nginx ..........: $NGINX"

  # --- 1. aplicativo -------------------------------------------------------
  install -d -o "$USUARIO" -g "$USUARIO" "$RAIZ/$RELEASE"
  tar -xzf "$PACOTE" -C "$RAIZ/$RELEASE"
  chown -R "$USUARIO:$USUARIO" "$RAIZ/$RELEASE"

  # O release em uso NAO tem appsettings.json: a configuracao vem dos arquivos de ambiente.
  # Levar um junto faria o aplicativo ler "Urls" e "Callback.Enabled" do pacote e ignorar o
  # ambiente - foi o que derrubou a terceira tentativa, tentando vincular a porta 18085.
  rm -f "$RAIZ/$RELEASE"/appsettings*.json
  DIFERENCA=$(comm -3 <(ls -1 "$ANTERIOR" | sort) <(ls -1 "$RAIZ/$RELEASE" | sort) | tr -d '\t' | tr '\n' ' ')
  if [ -n "$DIFERENCA" ]; then
    echo "FALHOU: o conjunto de arquivos difere do release atual: $DIFERENCA"
    echo "Nada foi reiniciado. Ajuste o pacote antes de instalar."
    rm -rf "$RAIZ/$RELEASE"; exit 1
  fi
  echo "conjunto de arquivos confere com $ANTERIOR"

  printf '%s\n' 'SdInbound__DevolutivaEnabled=true' > "$ENVFILE"
  chmod 0644 "$ENVFILE"

  # Reaproveita a linha de comando atual, trocando SO o diretorio do release. Assim "--urls" e
  # qualquer outro argumento seguem iguais. WorkingDirectory tambem acompanha: no ASP.NET a raiz
  # de conteudo vem do diretorio de trabalho, nao de onde esta a DLL.
  ARGV_NOVO=${ARGV_ANTIGO//$ANTERIOR/$RAIZ\/$RELEASE}
  if [ "$ARGV_NOVO" = "$ARGV_ANTIGO" ] || [ -z "$ARGV_NOVO" ]; then
    echo "FALHOU: nao consegui derivar a linha de comando do servico. Nada foi alterado."
    rm -rf "$RAIZ/$RELEASE"; exit 1
  fi
  echo "comando novo: $ARGV_NOVO"

  install -d "$(dirname "$DROPIN")"
  cat > "$DROPIN" <<EOF
[Service]
EnvironmentFile=$ENVFILE
WorkingDirectory=$RAIZ/$RELEASE
ExecStart=
ExecStart=$ARGV_NOVO
EOF

  systemctl daemon-reload

  # Confere que o ExecStart efetivo e mesmo o novo ANTES de reiniciar. Sem isto, um drop-in de
  # precedencia maior sobrescreve o nosso, o servico reinicia com o release antigo e a falha so
  # aparece depois - com o ACM interrompido a toa.
  if [ "$(atual)" != "$RAIZ/$RELEASE" ]; then
    echo "FALHOU antes de reiniciar: o ExecStart efetivo continua $(atual)."
    echo "Algum drop-in tem precedencia maior que $(basename "$DROPIN"). Nada foi reiniciado."
    rm -f "$DROPIN" "$ENVFILE"; systemctl daemon-reload; exit 1
  fi
  echo "ExecStart efetivo confere: $(atual)"

  systemctl restart "$SERVICO"

  # Espera o processo responder antes de tocar na porta de entrada.
  for _ in $(seq 1 30); do [ "$(saude)" = 200 ] && break; sleep 1; done
  if [ "$(saude)" != 200 ] || [ "$(rota_acm)" != 200 ]; then
    echo "FALHOU: saude=$(saude) rotaACM=$(rota_acm). Revertendo o aplicativo."
    rm -f "$DROPIN" "$ENVFILE"; systemctl daemon-reload; systemctl restart "$SERVICO"
    sleep 3; echo "voltou para: $(atual)  saude=$(saude)"; exit 1
  fi
  if [ "$(rota_app)" != 200 ]; then
    echo "FALHOU: a rota SD nao responde no aplicativo ($(rota_app)). Revertendo."
    rm -f "$DROPIN" "$ENVFILE"; systemctl daemon-reload; systemctl restart "$SERVICO"
    sleep 3; echo "voltou para: $(atual)"; exit 1
  fi
  echo "aplicativo OK: saude=200 rotaACM=200 rotaSD=200"

  # --- 2. porta de entrada -------------------------------------------------
  if grep -q "location = $ROTA " "$NGINX" || grep -q "location = $ROTA{" "$NGINX"; then
    echo "nginx: bloco da rota SD ja existe, nada a fazer."
  else
    cp -p "$NGINX" "$NGINX.bak-sd-$(date -u +%Y%m%d%H%M%SZ)"
    # Copia o bloco do ACM inteiro, trocando so o caminho: preserva cabecalhos, limites
    # e lista de origens exatamente como estao, em vez de eu reescreve-los de memoria.
    # O caminho vai como VARIAVEL, nao dentro de /.../: as barras de "/api/guardian/..." fecham
    # a expressao regular literal e o awk recusa. Como variavel, e tratado como texto.
    awk -v acm="location = $ROTA_ACM" -v caminho="$ROTA_ACM" -v nova="$ROTA" '
      $0 ~ acm && !feito { dentro=1; chaves=0 }
      # A linha ORIGINAL sai primeiro, e so entao a copia. Emitir a copia ao fechar as chaves
      # a colocava DENTRO do bloco do ACM, e o nginx recusa location aninhada em location exata.
      { print }
      dentro {
        linha=$0; sub(caminho, nova, linha); bloco = bloco linha "\n"
        chaves += gsub(/\{/,"{") - gsub(/\}/,"}")
        if (chaves==0) { printf "\n%s", bloco; bloco=""; dentro=0; feito=1 }
      }
    ' "$NGINX" > "$NGINX.novo"
    mv "$NGINX.novo" "$NGINX"
    if ! nginx -t 2>&1 | tail -2; then
      echo "FALHOU: nginx -t recusou. Restaurando."
      cp -p "$(ls -t "$NGINX".bak-sd-* | head -1)" "$NGINX"; exit 1
    fi
    systemctl reload nginx
    echo "nginx: bloco da rota SD publicado e recarregado."
  fi

  echo
  echo "=== RESULTADO ==="
  echo "release em uso: $(atual)"
  echo "saude $PORTA ...: $(saude)"
  echo "rota ACM ......: $(rota_acm)  (inalterada)"
  echo "rota SD .......: $(rota_app)"
  echo "Do lado de fora a rota so aceita as origens da lista do nginx."
  ;;

reverter)
  echo "Removendo o drop-in e voltando ao release anterior do aplicativo."
  rm -f "$DROPIN" "$ENVFILE"
  systemctl daemon-reload; systemctl restart "$SERVICO"; sleep 3
  echo "release em uso: $(atual)   saude=$(saude)  rotaACM=$(rota_acm)"
  NGINX=$(nginx_arquivo)
  ULTIMO=$(ls -t "$NGINX".bak-sd-* 2>/dev/null | head -1 || true)
  if [ -n "$ULTIMO" ]; then
    cp -p "$ULTIMO" "$NGINX" && nginx -t >/dev/null 2>&1 && systemctl reload nginx \
      && echo "nginx restaurado de $ULTIMO"
  else
    echo "nginx: sem backup para restaurar (bloco pode nao ter sido criado)."
  fi
  ;;

*) echo "Uso: sudo $0 {ensaio|nginx|conferir|instalar|reverter}"; exit 1 ;;
esac
