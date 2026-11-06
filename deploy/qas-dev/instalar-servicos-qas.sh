#!/usr/bin/env bash
# Cria o segundo conjunto de servicos do SD: receptor e motor do QAS120.
#
# NAO TOCA EM NENHUMA UNIDADE DO DEV110. As unidades do QAS sao NOVAS, com nomes proprios,
# porta propria e EnvironmentFile proprio. O DEV continua exatamente onde esta.
#
# DESENHO, e por que assim:
#
#   MESMO BINARIO, configuracao diferente. E o desenho que construimos hoje: o ambiente de um
#   processo e o par que ele tem configurado. Entao o QAS nao ganha um build proprio - ele aponta
#   para o release novo, que traz as guardas de ambiente. O DEV so passa a usar esse release
#   depois que o QAS provar que ele esta bom.
#
#   UM EnvironmentFile, e nao catorze drop-ins. O receptor do DEV acumulou 14 drop-ins ao longo
#   de semanas, numa disputa de precedencia por quantidade de "z" no nome. O QAS nasce com UM
#   arquivo, montado a partir das CHAVES que o DEV usa hoje - o estado, nao o historico.
#
#   O NGINX REESCREVE o caminho. As rotas sao constantes no codigo (/venda e /recebimento), entao
#   o receptor do QAS serve os MESMOS caminhos, na porta dele. Quem traduz /venda-qas120 para /venda
#   e o nginx - ver instalar-rotas-qas.sh. Mexer na constante criaria duas verdades no codigo.
#
# NENHUM SEGREDO E LIDO OU COPIADO. Do ambiente do DEV saem so os NOMES das chaves; todo valor
# vai como <PREENCHER> e quem preenche e voce. O "aplicar" recusa subir com <PREENCHER> sobrando.
#
# Uso:  sudo ./instalar-servicos-qas.sh {conferir|preparar|aplicar|reverter}
set -euo pipefail

PORTA_QAS="${PORTA_QAS:-18087}"
REL_CB="${REL_CB:-/opt/fugates/releases/FugaTES.Callback.Http}"
REL_MT="${REL_MT:-/opt/fugates/releases/FugaTES.HostIntegracao}"
PACOTE_CB="${PACOTE_CB:-/home/michael/fugates-callback.tgz}"
PACOTE_MT="${PACOTE_MT:-/home/michael/fugates-motor.tgz}"
SHA_CB='a0a8486f710e491b249e64e8c447d03e7c58132c8702ff8dc0d7b1edf56e1914'
SHA_MT='90bcfab098f4e59edd7bcb31e5745d528073e0ab4fd600126820b8923e68b84b'

DEV_CB=fugates-toledo-callback
DEV_MT=fugates-sd-protocolo
QAS_CB=fugates-callback-qas
QAS_MT=fugates-sd-protocolo-qas

# Sobrepor estes dois e o que torna o script ENSAIAVEL fora da VM, contra systemd falso. Tres
# scripts meus falharam hoje por nao ter sido ensaiados; ensaio deixou de ser opcional.
ENV_QAS="${ENV_QAS:-/etc/fugates/qas120}"
UNIDADES="${UNIDADES:-/etc/systemd/system}"

SAP_QAS_URL='https://vhfufqs4ci.sap.fugacouros.com.br:44300'
SAP_QAS_CLI='120'
BANCO_QAS='fugates_qas'
PAPEL_SD='fugates_qas_sd_app'
PAPEL_ACM='fugates_qas_acm_app'
PASSFILE="${PASSFILE:-/etc/fugates/qas120/pg.pass}"

[ "$(id -u)" = 0 ] || { echo "Rode com sudo."; exit 1; }

CONCLUIDO=0
FALHOU=""
# $LINENO dentro de um trap EXIT aponta para a linha do PROPRIO trap, nao para a que falhou: o
# primeiro ensaio acusou "linha 2". Numero errado desorienta mais do que numero nenhum, entao
# quem guarda a linha de verdade e o trap ERR. set -E faz ele valer dentro de funcoes e subshells.
set -E
trap 'FALHOU=$LINENO' ERR
trap '[ "$CONCLUIDO" = 1 ] || echo "
INTERROMPIDO na linha ${FALHOU:-?}, sem concluir. Defeito DESTE script: me mande esta saida." >&2' EXIT

usuario_de() { systemctl show "$1" -p User --value 2>/dev/null; }
ativo() { systemctl is-active "$1" 2>/dev/null || true; }

# As CHAVES de ambiente que uma unidade do DEV usa, sem nenhum valor.
chaves_de() {
  systemctl show "$1" -p EnvironmentFiles --value 2>/dev/null | tr ' ' '\n' | grep -E '^/' | while read -r f; do
    [ -f "$f" ] || continue
    grep -oE '^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=' "$f" 2>/dev/null | tr -d ' =' || true
  done | sort -u
}

# O ExecStart efetivo de uma unidade, para o QAS nascer com os MESMOS argumentos.
argv_de() {
  systemctl show "$1" -p ExecStart --value 2>/dev/null \
    | sed -n 's/.*argv\[\]=\(.*\) ; ignore_errors.*/\1/p' | head -1
}

# Type= e WorkingDirectory= da unidade do DEV, para o QAS nascer igual.
#
# NAO ADIVINHAR AQUI. Se o receptor do DEV e Type=simple e eu escrever notify, o systemd espera
# um aviso de prontidao que nunca chega, a unidade estoura o tempo e o diagnostico vai parecer
# erro de aplicacao. Derivar custa uma linha; adivinhar custa uma hora.
tipo_de() { systemctl show "$1" -p Type --value 2>/dev/null; }
dir_de()  { systemctl show "$1" -p WorkingDirectory --value 2>/dev/null; }

# O valor EFETIVO de uma chave na unidade do DEV: os EnvironmentFile sao aplicados em ordem e a
# ultima definicao vence, igual ao systemd. O receptor do DEV tem 14 drop-ins acumulados; ler o
# primeiro que casa daria o valor ERRADO para qualquer chave que tenha sido corrigida depois.
#
# O valor NUNCA e impresso: ele vai direto para o arquivo do QAS, root para root. Isto nao
# contraria a regra de nao exportar credencial - exportar e eu ver ou mandar para fora, e nada
# aqui sai do disco da VM nem passa pela tela.
valor_efetivo() {
  local u="$1" k="$2" achado="" f v
  while read -r f; do
    [ -f "$f" ] || continue
    v="$( { grep -E "^[[:space:]]*${k}=" "$f" 2>/dev/null | tail -1 | cut -d= -f2-; } || true )"
    if [ -n "$v" ]; then achado="$v"; fi
  done <<< "$(systemctl show "$u" -p EnvironmentFiles --value 2>/dev/null | tr ' ' '\n' | grep -E '^/' || true)"
  printf '%s' "$achado"
}

case "${1:-}" in

conferir)
  echo "=============================================================="
  echo " O QUE EXISTE HOJE"
  echo "=============================================================="
  printf '  %-34s %s\n' "$DEV_CB" "$(ativo "$DEV_CB")  usuario $(usuario_de "$DEV_CB")"
  printf '  %-34s %s\n' "$DEV_MT" "$(ativo "$DEV_MT")  usuario $(usuario_de "$DEV_MT")"
  printf '  %-34s %s\n' "$QAS_CB" "$(ativo "$QAS_CB" || echo 'nao existe')"
  printf '  %-34s %s\n' "$QAS_MT" "$(ativo "$QAS_MT" || echo 'nao existe')"
  echo
  echo "porta $PORTA_QAS para o receptor do QAS:"
  if ss -lnt 2>/dev/null | grep -q ":$PORTA_QAS "; then
    echo "  OCUPADA - escolha outra com PORTA_QAS=..."
  else
    echo "  livre"
  fi
  echo
  echo "chaves de ambiente que o receptor do DEV usa (NOMES, nunca valores):"
  chaves_de "$DEV_CB" | sed 's/^/   /'
  echo
  echo "chaves de ambiente que o motor do DEV usa:"
  chaves_de "$DEV_MT" | sed 's/^/   /'
  echo
  echo "pacotes:"
  for p in "$PACOTE_CB" "$PACOTE_MT"; do
    if [ -f "$p" ]; then
      echo "  $(basename "$p") presente, sha $(sha256sum "$p" | cut -c1-16)..."
    else
      echo "  $(basename "$p") AUSENTE"
    fi
  done
  CONCLUIDO=1
  ;;

preparar)
  for par in "$PACOTE_CB:$SHA_CB" "$PACOTE_MT:$SHA_MT"; do
    p="${par%%:*}"; s="${par##*:}"
    [ -f "$p" ] || { echo "Pacote ausente: $p"; CONCLUIDO=1; exit 1; }
    [ "$(sha256sum "$p" | cut -d' ' -f1)" = "$s" ] || { echo "SHA nao confere: $p"; CONCLUIDO=1; exit 1; }
  done
  echo "== pacotes conferidos"

  MARCA="QAS120_$(date -u +%Y%m%d_%H%M%SZ)"
  echo "== extraindo os releases do QAS (o DEV nao e tocado)"
  install -d -m 0755 "$REL_CB/$MARCA" "$REL_MT/$MARCA"
  tar -xzf "$PACOTE_CB" -C "$REL_CB/$MARCA"
  tar -xzf "$PACOTE_MT" -C "$REL_MT/$MARCA"
  U="$(usuario_de "$DEV_CB")"; [ -n "$U" ] || U=fugates-http
  chown -R "$U":"$U" "$REL_CB/$MARCA" "$REL_MT/$MARCA" 2>/dev/null || true

  # O BIT DE EXECUCAO NAO SOBREVIVE AO PACOTE.
  #
  # Os .tgz sao montados no Windows, que nao tem bit de execucao; o tar extrai o apphost com
  # 0644 e o systemd falha com 203/EXEC - "Main process exited", sem uma linha do aplicativo,
  # porque o aplicativo nunca chegou a rodar. O instalador do DEV ja fazia este chmod; eu nao
  # copiei essa linha quando escrevi este.
  #
  # PELO CONTEUDO, E NAO PELO NOME.
  #
  # A primeira versao pulava todo arquivo com ponto no nome, "porque apphost nao tem extensao".
  # O apphost daqui se chama FugaTES.HostIntegracao - tem ponto - e era justamente o arquivo que
  # a busca existia para achar. Pior: ela imprimia "nenhum apphost ELF", que se le como um fato
  # sobre o pacote e nao como o filtro tendo descartado tudo.
  #
  # Os quatro bytes magicos do ELF nao dependem de convencao de nome. So .dll, .so, .json e .pdb
  # sao pulados antes, para a saida nao listar biblioteca.
  EXECUTAVEIS=0
  for raiz in "$REL_CB/$MARCA" "$REL_MT/$MARCA"; do
    while IFS= read -r arq; do
      case "$(basename "$arq")" in *.dll|*.so|*.json|*.pdb|*.xml) continue ;; esac
      head -c4 "$arq" 2>/dev/null | grep -q $'\x7fELF' || continue
      chmod +x "$arq"
      echo "   executavel . $arq"
      EXECUTAVEIS=$((EXECUTAVEIS+1))
    done < <(find "$raiz" -maxdepth 1 -type f)
  done
  [ "$EXECUTAVEIS" -gt 0 ] || echo "   (nenhum apphost ELF na raiz dos releases - o ExecStart deve usar o dotnet)"

  echo "   receptor ... $REL_CB/$MARCA"
  echo "   motor ...... $REL_MT/$MARCA"

  install -d -m 0750 "$ENV_QAS"

  for alvo in cb mt; do
    case "$alvo" in
      cb) DEV="$DEV_CB"; ARQ="$ENV_QAS/callback.env"; PAPEL="$PAPEL_ACM" ;;
      mt) DEV="$DEV_MT"; ARQ="$ENV_QAS/motor.env";    PAPEL="$PAPEL_SD" ;;
    esac
    if [ -f "$ARQ" ]; then echo "== $ARQ ja existe, preservado"; continue; fi

    HERDADAS=0; TROCADAS=""
    {
      echo "# Ambiente do QAS120 - gerado em $(date -u +%Y-%m-%dT%H:%M:%SZ) a partir do $DEV."
      echo "#"
      echo "# OS VALORES FORAM HERDADOS, e so os que TEM de diferir foram trocados. O receptor do"
      echo "# DEV tem 59 chaves, a maioria campo de aprovacao (ApprovalReference, NotBeforeUtc,"
      echo "# ExecutionAuthorized, SecurityMode). Pedir 50 delas preenchidas a mao e como se"
      echo "# desliga uma guarda sem perceber - e voce ja decidiu reaproveitar as aprovacoes do"
      echo "# DEV110 neste ambiente."
      echo "#"
      echo "# O que foi trocado esta marcado com QAS120 na linha acima da chave."
      echo
      while IFS= read -r k; do
        v="$(valor_efetivo "$DEV" "$k")"
        case "$k" in
          ASPNETCORE_URLS|ASPNETCORE_HTTP_PORTS|Urls)
            echo "# QAS120: porta propria. Sem isto a unidade do QAS disputaria a porta do DEV."
            echo "$k=http://127.0.0.1:$PORTA_QAS"
            TROCADAS="$TROCADAS $k" ;;

          *PrivateListenerPort)
            echo "# QAS120: a porta deste receptor."
            echo "#"
            echo "# ATENCAO: a admissao do /recebimento compara a porta local com 18086 ESCRITO NO"
            echo "# CODIGO (ToledoCallbackAdmission, linhas 36 e 60), e exige Environment=DEV. Com"
            echo "# $PORTA_QAS ela recusa com TOLEDO_SOURCE_NOT_ALLOWED. Isso NAO afeta o SD: a rota"
            echo "# /venda nao passa por essa porteira - ela e a lista do nginx mais o loopback."
            echo "# Para o /recebimento-qas120 funcionar, o par (Environment, porta) precisa ser"
            echo "# estendido no codigo, do mesmo jeito que o par (banco, papel) foi hoje."
            echo "$k=$PORTA_QAS"
            TROCADAS="$TROCADAS $k" ;;

          Sap__BaseUrl)   echo "# QAS120"; echo "$k=$SAP_QAS_URL"; TROCADAS="$TROCADAS $k" ;;
          Sap__SapClient) echo "# QAS120"; echo "$k=$SAP_QAS_CLI"; TROCADAS="$TROCADAS $k" ;;

          # A fabrica exige Passfile e Password NULO (HasExplicitAcmPassfile), e Search Path
          # fugates. Senha na cadeia faz recusar com R165R_EXPLICIT_ACM_PASSFILE_REQUIRED.
          *ConnectionString|ConnectionStrings__*)
            echo "# QAS120: banco e papel do QAS. A senha NAO vem aqui - vem do $PASSFILE,"
            echo "# porque a fabrica exige Passfile com Password nulo."
            echo "$k=Host=127.0.0.1;Port=5433;Database=$BANCO_QAS;Search Path=fugates;Username=$PAPEL;Enlist=false;Passfile=$PASSFILE"
            TROCADAS="$TROCADAS $k" ;;

          PGPASSFILE)
            echo "# QAS120: o passfile deste ambiente."
            echo "$k=$PASSFILE"
            TROCADAS="$TROCADAS $k" ;;

          # Diretorio de armazenamento compartilhado entre os dois ambientes seria o cruzamento
          # pelo disco: um grava o que o outro le, os dois com permissao legitima.
          *StoreDirectory|*Directory)
            echo "# QAS120: diretorio proprio - compartilhar com o DEV seria cruzar pelo disco."
            echo "$k=${v%/}-qas120"
            TROCADAS="$TROCADAS $k" ;;

          # A rota de retorno vai DENTRO do pre-cadastro que o FugaTES manda ao Guardian: e ela
          # que diz para onde devolver a pesagem. Por isso define em qual ambiente a devolutiva
          # cai, e por isso nao pode ser herdada.
          #
          # O QUE ELA TEM NO DEV NAO E NECESSARIAMENTE DERIVAVEL. Em 06/11/2026 o motor do DEV
          # tinha "https://webhook.site/<uuid>" aqui - um servico publico de captura, sem caminho
          # /venda nenhum. A troca por sufixo nao casou, e o valor foi copiado inteiro para o
          # QAS: o motor do QAS mandaria o Guardian devolver as pesagens dele para um endereco
          # publico na internet. A guarda do aplicar pegou, mas tarde demais para ser util.
          #
          # Entao: converte quando reconhece o caminho, e PEDE quando nao reconhece. Nunca copia.
          *UrlParaRetorno|*UrlRetorno|*CallbackUrl)
            convertida="$(printf '%s' "$v" | sed -E 's#/(venda|recebimento)/?$#/\1-qas120#')"
            if [ "$convertida" != "$v" ]; then
              echo "# QAS120: mesmo host, caminho do QAS."
              echo "$k=$convertida"
            else
              echo "# QAS120: NAO consegui converter o valor que o DEV tem aqui - ele nao termina"
              echo "# em /venda nem /recebimento, entao nao da para saber qual e o equivalente no"
              echo "# QAS. Copiar o do DEV faria o motor do QAS devolver no endereco do DEV (ou"
              echo "# no que estiver configurado la), entao ele nao foi copiado."
              echo "#"
              echo "# Ponha o endereco que o Guardian usa para alcancar ESTA VM, com o caminho do"
              echo "# QAS. O aplicar confere que termina em -qas120."
              echo "$k=http://<PREENCHER_HOST>/venda-qas120"
            fi
            TROCADAS="$TROCADAS $k" ;;

          *) printf '%s=%s\n' "$k" "$v"; HERDADAS=$((HERDADAS+1)) ;;
        esac
      done < <(chaves_de "$DEV")
    } > "$ARQ"
    chmod 0640 "$ARQ"
    chown root:"$U" "$ARQ" 2>/dev/null || true

    echo "== $(basename "$ARQ"): $HERDADAS chave(s) herdada(s) do DEV, $(printf '%s' "$TROCADAS" | wc -w) trocada(s):"
    for k in $TROCADAS; do printf '      %s\n' "$k"; done
  done

  # O PASSFILE. A senha e sua: voce a definiu com \password, e eu nao a leio nem a escrevo.
  # Formato do libpq: host:porta:banco:usuario:senha, uma linha por papel, modo 0600.
  if [ -f "$PASSFILE" ]; then
    echo "== $PASSFILE ja existe, preservado"
  else
    {
      echo "# Passfile do QAS120. Formato do libpq: host:porta:banco:usuario:senha"
      echo "# Modo 0600 e obrigatorio - o libpq ignora o arquivo em silencio se estiver mais aberto."
      echo "127.0.0.1:5433:$BANCO_QAS:$PAPEL_ACM:<PREENCHER_SENHA>"
      echo "127.0.0.1:5433:$BANCO_QAS:$PAPEL_SD:<PREENCHER_SENHA>"
    } > "$PASSFILE"
    chmod 0600 "$PASSFILE"
    chown "$U":"$U" "$PASSFILE" 2>/dev/null || true
    echo "== $PASSFILE criado (modo 0600, dono $U)"
  fi

  echo
  echo "=============================================================="
  echo " FALTA VOCE PREENCHER - um arquivo so"
  echo "=============================================================="
  echo "  sudo nano $PASSFILE"
  echo
  echo "  Duas linhas, as senhas que voce definiu com \password para:"
  echo "     $PAPEL_ACM   (receptor)"
  echo "     $PAPEL_SD    (motor)"
  echo
  echo "  Os dois .env ja estao completos: nada de <PREENCHER> neles."
  echo "  Confira se quiser:  sudo diff <(sudo cat $ENV_QAS/motor.env) /dev/null | head -40"
  echo
  echo "  Marca deste release:  $MARCA"
  echo "  Depois:  sudo $0 aplicar MARCA=$MARCA"
  CONCLUIDO=1
  ;;
aplicar)
  # A MARCA VEM DO ARGUMENTO, E NAO SO DO AMBIENTE.
  #
  # "cmd aplicar MARCA=x" NAO define variavel nenhuma: o shell so trata ATRIBUICAO ANTES do
  # comando ("MARCA=x cmd aplicar"); depois do subcomando, "MARCA=x" e texto posicional. Eu
  # ensaiei com a forma de prefixo e DOCUMENTEI a de sufixo - entao o ensaio passou e a primeira
  # chamada de verdade caiu em "Informe a marca", logo depois de informar a marca.
  #
  # Aceita as tres formas, porque discutir com o operador sobre sintaxe de shell nao e o trabalho.
  MARCA="${MARCA:-}"
  case "${2:-}" in
    MARCA=*) MARCA="${2#MARCA=}" ;;
    QAS120_*) MARCA="$2" ;;
  esac
  [ -n "$MARCA" ] || {
    echo "Informe a marca, de qualquer um destes jeitos:"
    echo "   sudo $0 aplicar MARCA=QAS120_..."
    echo "   sudo $0 aplicar QAS120_..."
    echo
    echo "As que existem agora:"
    ls -1 "$REL_CB" 2>/dev/null | grep '^QAS120_' | sed 's/^/   /' || echo "   (nenhuma: rode preparar)"
    CONCLUIDO=1; exit 1; }
  [ -d "$REL_CB/$MARCA" ] && [ -d "$REL_MT/$MARCA" ] || { echo "Release $MARCA nao existe. Rode preparar."; CONCLUIDO=1; exit 1; }

  for a in "$ENV_QAS/callback.env" "$ENV_QAS/motor.env"; do
    [ -f "$a" ] || { echo "Falta $a. Rode preparar."; CONCLUIDO=1; exit 1; }
    # SO LINHAS DE VALOR. O primeiro ensaio reprovou exatamente aqui: o marcador aparece no
    # COMENTARIO que explica o marcador, entao o aplicar recusava para sempre - e a recusa falsa
    # escondeu as duas conferencias seguintes, que nunca chegaram a rodar. Guarda que nunca passa
    # nao e guarda, e porta trancada; e uma recusa errada mascara as corretas que vem depois.
    if grep -v '^[[:space:]]*#' "$a" | grep -q '<PREENCHER'; then
      echo "AINDA HA <PREENCHER> em $a:"
      grep -n '<PREENCHER' "$a" | grep -v ':[[:space:]]*#' | sed -E 's/=.*/=<PREENCHER...>/' | sed 's/^/   /'
      echo "Nada foi alterado."
      CONCLUIDO=1; exit 1
    fi
  done
  echo "== ambiente preenchido nos dois arquivos"

  # O PASSFILE. Agora e o UNICO arquivo que depende de voce, e por isso o unico onde o
  # <PREENCHER> importa de verdade.
  [ -f "$PASSFILE" ] || { echo "Falta $PASSFILE. Rode preparar."; CONCLUIDO=1; exit 1; }
  if grep -v '^[[:space:]]*#' "$PASSFILE" | grep -q '<PREENCHER'; then
    echo "O $PASSFILE ainda tem <PREENCHER>."
    echo "Preencha as senhas dos dois papeis do QAS120 e rode aplicar de novo. Nada foi alterado."
    CONCLUIDO=1; exit 1
  fi
  # 0600 OU O LIBPQ IGNORA EM SILENCIO. Esta e a pior falha possivel aqui: o arquivo existe,
  # esta certo, e simplesmente nao e lido - e o erro que aparece e "autenticacao falhou", que
  # manda a gente procurar senha errada em vez de permissao errada.
  MODO="$(stat -c '%a' "$PASSFILE" 2>/dev/null || echo '?')"
  if [ "$MODO" != 600 ]; then
    echo "O $PASSFILE esta em modo $MODO; o libpq exige 600 e IGNORA o arquivo em silencio se"
    echo "estiver mais aberto - o erro apareceria como 'autenticacao falhou'. Corrija com:"
    echo "   sudo chmod 600 $PASSFILE"
    echo "Nada foi alterado."
    CONCLUIDO=1; exit 1
  fi
  # Uma linha por papel, com os cinco campos, e sem senha vazia. Nenhuma senha e impressa:
  # o que se confere e a CONTAGEM de campos e se o quinto nao esta vazio.
  for pp in "$PAPEL_ACM" "$PAPEL_SD"; do
    linha="$(grep -E "^127\.0\.0\.1:5433:$BANCO_QAS:$pp:" "$PASSFILE" | tail -1 || true)"
    if [ -z "$linha" ]; then
      echo "O $PASSFILE nao tem linha para 127.0.0.1:5433:$BANCO_QAS:$pp"; FALTA=1; continue
    fi
    if [ -z "${linha##*:}" ]; then
      echo "A linha de $pp no $PASSFILE esta com a senha vazia."; FALTA=1
    fi
  done
  echo "== passfile em modo 600, com linha para os dois papeis do QAS120"

  # NENHUMA CHAVE HERDADA PODE TER FICADO VAZIA.
  #
  # A contagem de chaves fechar (49+8=57 no receptor) prova que nada se PERDEU - nao prova que os
  # valores foram LIDOS. Se valor_efetivo falhasse numa chave, ela sairia como "Chave=" e seria
  # contada como herdada: um ApprovalReference vazio, um ExecutionAuthorized vazio. Nenhum deles
  # da erro; eles DESLIGAM a guarda, e o servico sobe dizendo que esta tudo bem.
  #
  # Esta e a sexta vez hoje que um verificador precisa conferir o EFEITO em vez do codigo de
  # retorno. So os NOMES sao impressos; valor nenhum aparece.
  #
  # Chave vazia no DEV e legitima e nao e acusada: o que se compara e com a origem.
  for a in "$ENV_QAS/callback.env" "$ENV_QAS/motor.env"; do
    case "$a" in *callback.env) DEVO="$DEV_CB" ;; *) DEVO="$DEV_MT" ;; esac
    while IFS= read -r k; do
      if [ -n "$(valor_efetivo "$DEVO" "$k")" ]; then
        echo "$(basename "$a"): $k ficou VAZIA, mas tem valor no DEV."
        echo "   Uma aprovacao vazia nao da erro: ela desliga a guarda."
        FALTA=1
      fi
    done < <(grep -vE '^[[:space:]]*#|^[[:space:]]*$' "$a" | grep -E '=[[:space:]]*$' | cut -d= -f1)
  done
  [ "${FALTA:-0}" = 0 ] && echo "== nenhuma chave herdada ficou vazia"

  # PGPASSWORD DEFINIDO FAZ A FABRICA RECUSAR (R165R_EXPLICIT_ACM_PASSFILE_REQUIRED): uma senha
  # no ambiente poderia sobrepor o passfile, e o codigo prefere nao subir a subir sem saber de
  # onde veio a credencial.
  for a in "$ENV_QAS/callback.env" "$ENV_QAS/motor.env"; do
    if grep -qE '^[[:space:]]*PGPASSWORD=' "$a"; then
      echo "$(basename "$a") define PGPASSWORD: a fabrica recusa com"
      echo "   R165R_EXPLICIT_ACM_PASSFILE_REQUIRED. Remova a linha."; FALTA=1
    fi
  done

  # O PAR (banco, papel) E A ROTA DE RETORNO, CONFERIDOS ANTES DE SUBIR.
  #
  # As guardas no codigo ja recusam um par cruzado - o servico simplesmente nao sobe. Mas a
  # mensagem que chega ao journal e um codigo, e quem esta na VM as 19h nao quer decifrar
  # SD_MOTOR_BANCO_NAO_CONFERE_COM_O_AMBIENTE. Conferir aqui diz o que esta errado, por nome.
  #
  # Senha nenhuma e lida: o grep pega Database= e Username=, nunca Password=.
  for a in "$ENV_QAS/callback.env" "$ENV_QAS/motor.env"; do
    while IFS= read -r linha; do
      case "$linha" in \#*|"") continue ;; esac
      valor="${linha#*=}"
      case "$linha" in
        *ConnectionString*=*|ConnectionStrings__*=*)
          b="$(printf '%s' "$valor" | tr ';' '\n' | grep -iE '^ *Database *=' | cut -d= -f2- | tr -d ' ')"
          p="$(printf '%s' "$valor" | tr ';' '\n' | grep -iE '^ *Username *=' | cut -d= -f2- | tr -d ' ')"
          [ "$b" = "$BANCO_QAS" ] || { echo "$(basename "$a"): Database=\"$b\"; o QAS120 e \"$BANCO_QAS\"."; FALTA=1; }
          case "$p" in
            fugates_qas_*) ;;
            *) echo "$(basename "$a"): Username=\"$p\" nao e papel do QAS120 (fugates_qas_*)."; FALTA=1 ;;
          esac
          # SENHA NA CADEIA FAZ RECUSAR. HasExplicitAcmPassfile exige Password NULO e um Passfile
          # absoluto; com senha na cadeia o servico nao sobe, e a mensagem e
          # R165R_EXPLICIT_ACM_PASSFILE_REQUIRED - que nao diz "tire a senha da cadeia".
          if printf '%s' "$valor" | tr ';' '\n' | grep -qiE '^ *Password *='; then
            echo "$(basename "$a"): a cadeia tem Password=. A fabrica exige Password NULO e"
            echo "   Passfile=; com senha na cadeia o servico recusa subir. A senha vai no passfile."
            FALTA=1
          fi
          if ! printf '%s' "$valor" | tr ';' '\n' | grep -qiE '^ *Passfile *= *\/'; then
            echo "$(basename "$a"): a cadeia nao tem Passfile= absoluto. Esperado: $PASSFILE"; FALTA=1
          fi
          # ENLIST PRECISA ESTAR ESCRITO, PORQUE O PADRAO DELE E "true".
          #
          # ValidateDedicatedBinding recusa a cadeia quando Enlist esta ligado - ele liga o
          # auto-alistamento em TransactionScope ambiente, e o vinculo dedicado nao admite isso.
          # Como o Npgsql assume true quando a chave FALTA, uma cadeia sem Enlist e recusada
          # exatamente como uma com Enlist=true: a ausencia nao e neutra.
          #
          # Foi o que derrubou o receptor do QAS em 06/11/2026. A cadeia que eu gerava tinha
          # banco, papel, porta, schema e passfile certos; faltava uma chave que ninguem escreve
          # porque "nao esta la" parece significar "desligado".
          if ! printf '%s' "$valor" | tr ';' '\n' | grep -qiE '^ *Enlist *= *false'; then
            echo "$(basename "$a"): a cadeia nao declara Enlist=false. O padrao do Npgsql e TRUE,"
            echo "   e o vinculo dedicado recusa com Enlist ligado - faltar e o mesmo que ligado."
            FALTA=1
          fi
          # Search Path: CriarAcm (receptor) EXIGE "fugates". O motor SD aceita ausente, mas aqui
          # as duas cadeias nascem com ele, entao divergir e sinal de edicao a mao.
          sp="$(printf '%s' "$valor" | tr ';' '\n' | grep -iE '^ *Search ?Path *=' | cut -d= -f2- | tr -d ' ' || true)"
          if [ -n "$sp" ] && [ "$sp" != fugates ]; then
            echo "$(basename "$a"): Search Path=\"$sp\"; o esperado e \"fugates\"."; FALTA=1
          fi ;;
        *UrlParaRetorno=*|*UrlRetorno=*|*CallbackUrl=*)
          case "$valor" in
            *-qas120) ;;
            *) echo "$(basename "$a"): a URL de retorno nao termina em -qas120: \"$valor\""
               echo "   Um motor do QAS devolvendo na rota do DEV grava no banco do DEV."; FALTA=1 ;;
          esac ;;
        Sap__SapClient=*)
          [ "$valor" = "$SAP_QAS_CLI" ] || { echo "$(basename "$a"): Sap__SapClient=\"$valor\"; o QAS120 e $SAP_QAS_CLI."; FALTA=1; } ;;
      esac
    done < "$a"
  done
  [ "${FALTA:-0}" = 0 ] || { echo "Nada foi alterado."; CONCLUIDO=1; exit 1; }
  echo "== o par (banco, papel), o sap-client e a rota de retorno conferem com o QAS120"

  U="$(usuario_de "$DEV_CB")"; [ -n "$U" ] || U=fugates-http

  # Argumentos do DEV, com a porta trocada: o que o receptor do DEV recebe na linha de comando
  # e o que o do QAS precisa, menos a porta. Derivar evita esquecer uma chave que custou caro.
  ARGV_CB="$(argv_de "$DEV_CB" | sed "s|$REL_CB/[^/ ]*|$REL_CB/$MARCA|; s|http://127.0.0.1:[0-9]*|http://127.0.0.1:$PORTA_QAS|")"
  [ -n "$ARGV_CB" ] || { echo "Nao consegui ler o ExecStart do $DEV_CB."; CONCLUIDO=1; exit 1; }

  # A PORTA TEM DE ESTAR EM ALGUM LUGAR. Ela pode vir da linha de comando OU do ambiente - depende
  # de como o receptor do DEV foi configurado, e eu nao tenho como saber qual dos dois sem olhar.
  # Se nao estiver em nenhum, a unidade do QAS sobe tentando a porta do DEV e morre em
  # "address already in use". Melhor recusar aqui, com o nome do problema.
  if ! printf '%s' "$ARGV_CB" | grep -q ":$PORTA_QAS" \
     && ! grep -q ":$PORTA_QAS" "$ENV_QAS/callback.env"; then
    echo "A porta $PORTA_QAS nao aparece nem no ExecStart nem no callback.env."
    echo "Sem isso o receptor do QAS tentaria a porta do DEV. Como o DEV define a porta hoje:"
    echo "   ExecStart: $(argv_de "$DEV_CB")"
    echo "   e as chaves de ambiente dele estao em: sudo $0 conferir"
    echo "Acrescente a chave de porta no $ENV_QAS/callback.env e rode aplicar de novo."
    echo "Nada foi alterado."
    CONCLUIDO=1; exit 1
  fi

  # ENDURECIMENTO HERDADO, NAO INVENTADO. A primeira versao deste script escrevia
  # ProtectSystem=strict nas duas unidades por parecer prudente. Com strict o sistema de arquivos
  # inteiro fica somente-leitura: se o processo escreve um arquivo qualquer, ele morre - e o erro
  # aparece como falha de aplicacao, nao de unidade. O receptor do DEV roda ha semanas; o que ele
  # tem configurado e o que comprovadamente funciona com ESTE binario.
  #
  # Entao cada diretiva vem da unidade do DEV, e so e escrita se o DEV a tiver de fato.
  # O TEXTO DA UNIDADE, E NAO O RELATORIO DELA.
  #
  # Duas versoes anteriores liam "systemctl show -p <nome>", enumerando as propriedades que eu
  # tinha LEMBRADO. As duas falharam pela mesma razao, em formas diferentes:
  #
  #   RestrictAddressFamilies veio como "~" - a representacao de uma lista negada vazia no
  #   RELATORIO, que nao e um valor que se escreve num arquivo de unidade.
  #
  #   LoadCredential veio como "[unprintable]": o systemd RECUSA imprimir esse valor, de
  #   proposito. A unidade do QAS nasceu sem ela, o motor nao achou CREDENTIALS_DIRECTORY e
  #   morreu em SD_MOTOR_CREDENCIAIS_SYSTEMD_AUSENTES - com a unidade parecendo correta.
  #
  # "systemctl cat" devolve o TEXTO dos arquivos, a unidade e os drop-ins na ordem em que o
  # systemd os aplica. Copiar esse texto nao depende de eu saber o que procurar, e e a mesma
  # correcao que o bloco do nginx precisou: copiar o que esta la, em vez de montar parecido.
  #
  # O que NAO e copiado, e por que:
  #   ExecStart*        reescrito com o release e a porta do QAS
  #   EnvironmentFile   o QAS tem o seu, com um arquivo so
  #   WorkingDirectory  reescrito com o release do QAS
  #   SyslogIdentifier  ganha sufixo, para o journal distinguir os ambientes
  #   User/Group        escritos logo abaixo, derivados do DEV
  #   SetCredential     carrega o VALOR da credencial; copiar segredo nao e trabalho deste
  #                     script. Se o DEV usar essa forma, o QAS vai DIZER que falta credencial.
  herdar() {
    local u="$1"
    systemctl cat "$u" 2>/dev/null \
      | awk '/^\[Service\]/{dentro=1;next} /^\[/{dentro=0} dentro' \
      | grep -vE '^[[:space:]]*([#;]|$)' \
      | grep -vE '^[[:space:]]*(ExecStart|ExecStartPre|ExecStartPost|ExecStop|EnvironmentFile|WorkingDirectory|SyslogIdentifier|SetCredential|User|Group)[[:space:]]*=' \
      | sed "s|$REL_CB/[^/ ]*|$REL_CB/$MARCA|g; s|$REL_MT/[^/ ]*|$REL_MT/$MARCA|g"
  }

  echo "== escrevendo as unidades do QAS"

  # Argumentos do DEV com o release e a porta trocados. Vale para as duas unidades: derivar o
  # ExecStart evita que eu erre o nome do executavel ou esqueca um argumento.
  ARGV_MT="$(argv_de "$DEV_MT" | sed "s|$REL_MT/[^/ ]*|$REL_MT/$MARCA|")"
  [ -n "$ARGV_MT" ] || { echo "Nao consegui ler o ExecStart do $DEV_MT."; CONCLUIDO=1; exit 1; }

  DIR_MT="$(dir_de "$DEV_MT" | sed "s|$REL_MT/[^/ ]*|$REL_MT/$MARCA|")"
  DIR_CB="$(dir_de "$DEV_CB" | sed "s|$REL_CB/[^/ ]*|$REL_CB/$MARCA|")"

  # O rotulo do journal de cada unidade do DEV, para o do QAS sair com sufixo. Vazio e resposta:
  # sem rotulo no DEV, o QAS tambem nao ganha um inventado.
  ROTULO_CB="$(systemctl show "$DEV_CB" -p SyslogIdentifier --value 2>/dev/null)"
  ROTULO_MT="$(systemctl show "$DEV_MT" -p SyslogIdentifier --value 2>/dev/null)"

  # O QUE ESTA NO ExecStart TEM DE EXISTIR E SER EXECUTAVEL.
  #
  # O preparar ja da +x nos apphosts, mas isso cobre o que ele reconhece. Esta conferencia pega
  # o caso geral, porque pergunta pelo arquivo que a unidade VAI MESMO executar - seja o apphost,
  # seja o /usr/bin/dotnet.
  #
  # Sem ela, o erro e 203/EXEC: "Main process exited", sem uma linha do aplicativo, porque o
  # aplicativo nunca chegou a rodar. Quem le isso procura defeito de configuracao durante um bom
  # tempo antes de desconfiar da permissao de um arquivo.
  for argv in "$ARGV_CB" "$ARGV_MT"; do
    bin="${argv%% *}"
    if [ ! -f "$bin" ]; then
      echo "O ExecStart aponta para um arquivo que nao existe:"; echo "   $bin"
      echo "Nada foi alterado."; CONCLUIDO=1; exit 1
    fi
    if [ ! -x "$bin" ]; then
      echo "O ExecStart aponta para um arquivo SEM permissao de execucao:"
      echo "   $bin   (modo $(stat -c '%a' "$bin" 2>/dev/null))"
      echo "O systemd falharia com 203/EXEC, sem nenhuma linha do aplicativo. Corrija com:"
      echo "   sudo chmod +x $bin"
      echo "Nada foi alterado."; CONCLUIDO=1; exit 1
    fi
  done
  echo "== os dois executaveis do ExecStart existem e sao executaveis"

  {
    echo "[Unit]"
    echo "Description=FugaTES - receptor do Guardian, ambiente QAS120 (porta $PORTA_QAS)"
    echo "After=network-online.target postgresql.service"
    echo "Wants=network-online.target"
    echo
    echo "[Service]"
    # Padroes ANTES do herdar: se a unidade do DEV declarar os seus, os dela vencem - num arquivo
    # de unidade a ultima definicao e a que vale. Assim o QAS nao perde um Restart deliberado do
    # DEV so porque eu tinha um padrao.
    echo "Restart=on-failure"
    echo "RestartSec=5"
    herdar "$DEV_CB"
    echo "User=$U"
    echo "Group=$U"
    echo "EnvironmentFile=$ENV_QAS/callback.env"
    if [ -n "$DIR_CB" ]; then echo "WorkingDirectory=$DIR_CB"; fi
    echo "ExecStart=$ARGV_CB"
    # O rotulo do journal e a UNICA diretiva que NAO se herda igual: com os dois ambientes
    # gravando sob o mesmo nome, a linha do QAS seria lida como linha do DEV - e e no journal
    # que se descobre qual ambiente recusou o que.
    if [ -n "$ROTULO_CB" ]; then echo "SyslogIdentifier=$ROTULO_CB-qas120"; fi
    echo
    echo "[Install]"
    echo "WantedBy=multi-user.target"
  } > "$UNIDADES/$QAS_CB.service"

  {
    echo "[Unit]"
    echo "Description=FugaTES - motor de protocolos SD, ambiente QAS120"
    echo "After=network-online.target postgresql.service"
    echo "Wants=network-online.target"
    echo
    echo "[Service]"
    echo "Restart=on-failure"
    echo "RestartSec=10"
    herdar "$DEV_MT"
    echo "User=$U"
    echo "Group=$U"
    echo "EnvironmentFile=$ENV_QAS/motor.env"
    if [ -n "$DIR_MT" ]; then echo "WorkingDirectory=$DIR_MT"; fi
    echo "ExecStart=$ARGV_MT"
    if [ -n "$ROTULO_MT" ]; then echo "SyslogIdentifier=$ROTULO_MT-qas120"; fi
    echo
    echo "[Install]"
    echo "WantedBy=multi-user.target"
  } > "$UNIDADES/$QAS_MT.service"

  # A UNIDADE DO QAS LEVOU O QUE A DO DEV TEM? Conferir contando, e nao confiando no herdar.
  #
  # Em 06/11/2026 o motor do QAS subiu sem LoadCredential, porque minha lista de diretivas era o
  # que eu tinha LEMBRADO de enumerar. O erro que chegou foi
  # SD_MOTOR_CREDENCIAIS_SYSTEMD_AUSENTES, do lado do APLICATIVO - a unidade parecia correta, e
  # nada no systemd acusava a falta. Uma diretiva ausente nao gera aviso nenhum: ela so deixa de
  # acontecer.
  #
  # Nenhum VALOR de credencial e lido ou impresso aqui - so quantas entradas existem de cada lado.
  FALTOU_DIRETIVA=0
  for par in "$QAS_CB:$DEV_CB" "$QAS_MT:$DEV_MT"; do
    qas="${par%%:*}"; dev="${par##*:}"
    for p in LoadCredential ReadWritePaths RuntimeDirectory StateDirectory ProtectSystem Type; do
      # Contando do TEXTO dos dois lados. O "systemctl show" nao serve nem para contar: ele
      # devolve "[unprintable]" para LoadCredential - uma unica palavra onde podem existir tres
      # credenciais, e nenhuma onde existe uma.
      n_dev="$(systemctl cat "$dev" 2>/dev/null \
               | awk '/^\[Service\]/{d=1;next} /^\[/{d=0} d' \
               | grep -cE "^[[:space:]]*$p[[:space:]]*=" || true)"
      n_qas="$(grep -cE "^$p=" "$UNIDADES/$qas.service" 2>/dev/null || true)"
      if [ "$n_dev" != "$n_qas" ]; then
        printf '   %s: %s - o DEV tem %s entrada(s), a unidade do QAS ficou com %s\n' \
               "$qas" "$p" "$n_dev" "$n_qas"
        FALTOU_DIRETIVA=1
      fi
    done
  done
  if [ "$FALTOU_DIRETIVA" != 0 ]; then
    echo "A unidade do QAS nao levou tudo que a do DEV tem. As unidades foram escritas mas NAO"
    echo "subiram. Me mande estas linhas: e defeito do herdar, nao do seu ambiente."
    CONCLUIDO=1; exit 1
  fi
  echo "== as unidades do QAS levaram as mesmas diretivas de lista que as do DEV"

  echo "   as duas unidades, para voce conferir antes de subirem:"
  for u in "$QAS_CB" "$QAS_MT"; do
    echo "   --- $UNIDADES/$u.service"
    sed 's/^/      /' "$UNIDADES/$u.service"
  done
  echo

  systemctl daemon-reload
  echo "== subindo"
  # ENGOLIR O ERRO DE SUBIDA FOI O QUE DEIXOU O MOTOR PARADO SEM NINGUEM SABER.
  #
  # A versao anterior fazia "enable --now ... >/dev/null 2>&1 || true" nas duas unidades, e o
  # veredito final so conferia o HTTP do receptor. Em 06/11/2026 o motor ficou inativo e o script
  # imprimiu "Pronto" assim mesmo - exatamente o tipo de sucesso falso que passei o dia
  # perseguindo nas ferramentas dos outros.
  #
  # "enable" e "start" separados, porque so o segundo pode falhar de forma interessante, e o que
  # ele disser vai para a tela.
  for u in "$QAS_CB" "$QAS_MT"; do
    systemctl enable "$u" >/dev/null 2>&1 || true
    systemctl restart "$u" || echo "   o systemd recusou subir $u (veja o journal abaixo)"
  done
  sleep 4

  echo
  echo "== estado"
  printf '  %-34s %s\n' "$QAS_CB" "$(ativo "$QAS_CB")"
  printf '  %-34s %s\n' "$QAS_MT" "$(ativo "$QAS_MT")"
  echo
  echo "== a rota do QAS responde na porta $PORTA_QAS?"
  cod="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$PORTA_QAS/api/guardian/set26/callback/venda" || echo ERRO)"
  echo "   GET /venda = $cod   (200 = receptor pronto)"
  echo
  echo "== o DEV110 continua intocado?"
  printf '  %-34s %s\n' "$DEV_CB" "$(ativo "$DEV_CB")"
  printf '  %-34s %s\n' "$DEV_MT" "$(ativo "$DEV_MT")"
  echo
  # O VEREDITO COBRE OS DOIS SERVICOS, e nao so o que responde HTTP.
  #
  # Ate 06/11/2026 so o HTTP do receptor era conferido, e o motor podia ficar parado sob um
  # "Pronto". Um ambiente com metade dos servicos de pe nao esta pronto - e o motor e justamente
  # quem cria a remessa no SAP.
  PARADO=0
  for u in "$QAS_CB" "$QAS_MT"; do
    case "$(ativo "$u")" in
      active) ;;
      *) echo "O servico $u nao esta ativo. Veja o porque:"
         echo "   sudo journalctl -u $u -n 40 --no-pager"
         PARADO=1 ;;
    esac
  done
  if [ "$cod" != "200" ]; then
    echo "O receptor do QAS nao respondeu 200 na porta $PORTA_QAS. Veja o porque:"
    echo "   sudo journalctl -u $QAS_CB -n 40 --no-pager"
    PARADO=1
  fi
  [ "$PARADO" = 0 ] || { CONCLUIDO=1; exit 1; }
  echo "Pronto: os DOIS servicos do QAS120 estao ativos e a rota responde."
  echo "Falta a rota no nginx, se ainda nao: sudo ./instalar-rotas-qas.sh aplicar"
  CONCLUIDO=1
  ;;

reverter)
  echo "Isto para e remove as unidades do QAS. O DEV110 nao e tocado."
  printf 'Para confirmar, escreva exatamente: REVERTER QAS\n> '
  read -r r
  [ "$r" = "REVERTER QAS" ] || { echo "Nada foi alterado."; CONCLUIDO=1; exit 1; }
  for u in "$QAS_CB" "$QAS_MT"; do
    systemctl disable --now "$u" >/dev/null 2>&1 || true
    rm -f "$UNIDADES/$u.service"
  done
  systemctl daemon-reload
  echo "Unidades removidas. Os releases e o $ENV_QAS ficaram - apague a mao se quiser."
  CONCLUIDO=1
  ;;

*)
  echo "Uso: sudo $0 {conferir|preparar|aplicar|reverter}"
  CONCLUIDO=1; exit 1
  ;;
esac
