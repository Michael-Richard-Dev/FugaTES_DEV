<#
    R167 - Corte CONTROLADO do ingresso SAP existente para o receptor DEV, e volta.

    PRESERVA A URL ATUAL. O SAP continua enviando para
    http://192.168.17.40/api/v1/integracoes/sap/contratos, sem nenhuma reconfiguracao do lado dele.

    A UNICA alteracao e, dentro do location ja existente, trocar o destino interno:
        proxy_pass http://127.0.0.1:8080;   (HML)
      ->proxy_pass http://127.0.0.1:18764;  (receptor DEV unificado)

    IMPACTO CONCRETO, que o operador precisa aceitar antes:
      enquanto o corte estiver ativo, TODO contrato SAP que chegar nessa URL vai para o DEV e
      NAO para o HML. Por isso a janela deve ser curta, combinada com quem opera o SAP, e revertida
      logo apos o contrato de homologacao. Este script nao abre janela sozinho: exige -Confirmo.

    O QUE NAO FAZ: nao altera a allowlist de origem, nao altera as demais locations, nao mexe no
    servico HML, nao duplica trafego (nada de mirror, que causaria dupla criacao), nao injeta
    X-Forwarded-Proto=https - o trafego nao e TLS e isso seria declarar protecao inexistente.

    AUTENTICACAO: o location atual faz `proxy_set_header Authorization "";`, removendo qualquer
    credencial. O receptor DEV exige Basic. Com -InjetarBasic, o script passa a enviar ao receptor
    uma credencial de CANAL, lida do arquivo 600 da VM. Ela autentica o INGRESSO, nao o SAP, e isso
    precisa estar explicito para quem aprova. Sem -InjetarBasic o corte nao e aplicado, porque
    resultaria em 401 garantido.

    Uso:
      .\Cortar-IngressoSapParaDev.ps1 -Conferir             # le o estado atual, nada altera
      .\Cortar-IngressoSapParaDev.ps1 -InjetarBasic -Confirmo
      .\Cortar-IngressoSapParaDev.ps1 -Reverter -Confirmo   # volta ao HML
#>
[CmdletBinding()]
param(
    [switch]$Conferir,
    [switch]$InjetarBasic,
    [switch]$Reverter,
    [switch]$Confirmo,
    # Observacao: so liga/desliga o access_log de METADADOS no location do SAP. Nao muda destino,
    # allowlist nem autenticacao, e nao registra corpo. Serve para descobrir se o contrato de
    # homologacao pode ser isolado pelo IP de origem.
    [switch]$Observar,
    [switch]$PararObservacao,
    [int]$PortaDev = 18764,
    [string]$Endpoint = 'michael@192.168.17.40',
    [string]$IdentityFile = 'C:\ProgramData\FugaTES\agentic\ssh\fugates-agentic-nt-ti-256',
    [string]$KnownHosts = 'C:\Users\TI\.ssh\known_hosts'
)

$ErrorActionPreference = 'Stop'
$ssh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$base = @('-F','NUL','-o','IdentitiesOnly=yes','-o','IdentityAgent=none','-o','StrictHostKeyChecking=yes',
          '-o',"UserKnownHostsFile=$KnownHosts",'-o','GlobalKnownHostsFile=NUL','-o','ConnectTimeout=20','-i',$IdentityFile)
$lote = $base + @('-o','BatchMode=yes')
$conf = '/etc/nginx/sites-available/fugates-hml-http.conf'

$estado = [ordered]@{}
function Veredito([string]$chave, [string]$valor, [string]$detalhe = '') {
    $estado[$chave] = $valor
    $cor = if ($valor -like 'PASS*') { 'Green' } elseif ($valor -like 'BLOCKED*') { 'Red' } else { 'Yellow' }
    Write-Host ("{0,-28} {1}" -f $chave, $valor) -ForegroundColor $cor
    if ($detalhe) { Write-Host ("  " + $detalhe) }
}
function Resumo { Write-Host ''; Write-Host '=== RESUMO ===' -ForegroundColor Cyan
    $estado.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-28} {1}" -f $_.Key, $_.Value) } }
function Abortar([string]$chave, [string]$motivo) { Veredito $chave 'BLOCKED' $motivo; Resumo; exit 1 }

function ParaLf([string]$t) { $t -replace "`r`n", "`n" }
function Base64([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ParaLf $s))) }
function Remoto([string]$script, [string[]]$parametros = @()) {
    $comando = 'printf %s ' + (Base64 $script) + ' | base64 -d > /tmp/r167_ing.sh; bash /tmp/r167_ing.sh'
    if ($parametros -and $parametros.Count -gt 0) { $comando += ' ' + ($parametros -join ' ') }
    & $ssh @($lote + @('-n') + @($Endpoint) + @($comando))
}
function RemotoInterativo([string]$script, [string]$nome, [string[]]$parametros = @()) {
    $instalar = 'umask 077; printf %s ' + (Base64 $script) + " | base64 -d > /tmp/$nome; chmod 700 /tmp/$nome"
    & $ssh @($lote + @('-n') + @($Endpoint) + @($instalar)) | Out-Null
    if ($LASTEXITCODE -ne 0) { Abortar 'ROTEIRO_REMOTO' "nao foi possivel gravar /tmp/$nome na VM" }
    & $ssh @($base + @('-tt') + @($Endpoint) + @("bash /tmp/$nome " + ($parametros -join ' ')))
}

Write-Host '=== R167 - corte do ingresso SAP para o receptor DEV ===' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------- estado atual
$estadoScript = @'
set -eu
C=/etc/nginx/sites-available/fugates-hml-http.conf
echo "DESTINO:$(sed -n '/location = \/api\/v1\/integracoes\/sap\/contratos/,/^    }/p' $C | sed -n 's/.*proxy_pass http:\/\/127.0.0.1:\([0-9]*\).*/\1/p' | head -1)"
echo "AUTHORIZATION:$(sed -n '/location = \/api\/v1\/integracoes\/sap\/contratos/,/^    }/p' $C | grep -c 'proxy_set_header Authorization ""')"
echo "PROTO:$(sed -n '/location = \/api\/v1\/integracoes\/sap\/contratos/,/^    }/p' $C | sed -n 's/.*X-Forwarded-Proto \([a-z]*\);.*/\1/p' | head -1)"
echo "HML8080:$(ss -ltn | grep -c 127.0.0.1:8080)"
echo "DEVPORTA:$(ss -ltn | grep -c 127.0.0.1:__PORTA__)"
echo "BACKUPS:$(ls -1 /etc/nginx/sites-available/fugates-hml-http.conf.backup.* 2>/dev/null | wc -l)"
'@
$atual = @(Remoto ($estadoScript -replace '__PORTA__', $PortaDev))
if ($LASTEXITCODE -ne 0 -or $atual.Count -lt 5) { Abortar 'ESTADO' "leitura remota falhou: $($atual -join ' / ')" }
$destino = ($atual | Where-Object { $_ -like 'DESTINO:*' }) -replace '^DESTINO:', ''
$devNoAr = (($atual | Where-Object { $_ -like 'DEVPORTA:*' }) -replace '^DEVPORTA:', '') -ne '0'
$hmlNoAr = (($atual | Where-Object { $_ -like 'HML8080:*' }) -replace '^HML8080:', '') -ne '0'
Veredito 'ESTADO' 'PASS' ("destino atual=127.0.0.1:{0} | HML 8080 no ar={1} | receptor DEV {2} no ar={3}" -f $destino, $hmlNoAr, $PortaDev, $devNoAr)

if ($Conferir) {
    Write-Host ''
    Write-Host 'Modo -Conferir: nada foi alterado.' -ForegroundColor Yellow
    Resumo; exit 0
}

if (-not $Confirmo) {
    Write-Host ''
    Write-Host 'Esta operacao muda para onde vao os contratos SAP reais desta URL.' -ForegroundColor Red
    Write-Host 'Repita com -Confirmo apenas apos autorizacao explicita do operador.' -ForegroundColor Red
    Abortar 'AUTORIZACAO' '-Confirmo ausente'
}

if ($Observar -or $PararObservacao) {
    # Observacao: acrescenta (ou remove) APENAS a linha de access_log no location do SAP, usando o
    # formato fugates_toledo_route que ja existe neste Nginx. Esse formato registra peer, horario,
    # metodo, path, presenca de query e status - NAO registra corpo, header nem credencial.
    # Nao altera destino, allowlist nem autenticacao: o trafego continua indo para o HML.
    # Serve para saber se o contrato de homologacao pode ser distinguido pelo IP de origem.
    $acao = if ($Observar) { 'ligar' } else { 'desligar' }
    $observarScript = @'
set -eu
C=/etc/nginx/sites-available/fugates-hml-http.conf
ACAO="$1"
BK=$C.backup.r167obs.$(date -u +%Y%m%d_%H%M%SZ)
sudo -p 'Senha sudo do DEV: ' cp -p "$C" "$BK"
if [ "$ACAO" = ligar ]; then
  sudo awk '
    /location = \/api\/v1\/integracoes\/sap\/contratos/ { dentro=1; print; next }
    dentro && /access_log/ { print; achou=1; next }
    dentro && /^    }/ { if (!achou) print "        access_log /var/log/nginx/fugates-sap-ingress.log fugates_toledo_route;"; dentro=0 }
    { print }
  ' "$BK" > /tmp/r167_obs.conf
else
  sudo awk '
    /location = \/api\/v1\/integracoes\/sap\/contratos/ { dentro=1 }
    dentro && /access_log \/var\/log\/nginx\/fugates-sap-ingress.log/ { next }
    dentro && /^    }/ { dentro=0 }
    { print }
  ' "$BK" > /tmp/r167_obs.conf
fi
sudo cp /tmp/r167_obs.conf "$C"; sudo rm -f /tmp/r167_obs.conf
sudo nginx -t
sudo systemctl reload nginx
echo "BACKUP:$BK"
echo "OBSERVACAO:$(sed -n '/location = \/api\/v1\/integracoes\/sap\/contratos/,/^    }/p' $C | grep -c fugates-sap-ingress.log)"
echo "DESTINO:$(sed -n '/location = \/api\/v1\/integracoes\/sap\/contratos/,/^    }/p' $C | sed -n 's/.*proxy_pass http:\/\/127.0.0.1:\([0-9]*\).*/\1/p' | head -1)"
'@
    RemotoInterativo $observarScript 'r167_obs.sh' @($acao)
    if ($LASTEXITCODE -ne 0) { Abortar 'OBSERVACAO' 'nao foi possivel alterar a observacao; o nginx -t barra configuracao invalida antes do reload' }
    Veredito 'OBSERVACAO' 'PASS' ("{0}; destino permanece no HML e nenhum corpo e registrado" -f $acao)
    Resumo; exit 0
}

if ($Reverter) {
    $reverterScript = @'
set -eu
C=/etc/nginx/sites-available/fugates-hml-http.conf
B=$(ls -1t $C.backup.r167.* 2>/dev/null | head -1)
test -n "$B" || { echo "SEM_BACKUP_R167"; exit 1; }
sudo -p 'Senha sudo do DEV: ' cp "$B" "$C"
sudo nginx -t
sudo systemctl reload nginx
# O backup restaurado nao referencia mais o include; o arquivo de segredo fica orfao e e removido,
# para nao deixar credencial de canal viva depois da janela.
sudo rm -f /etc/nginx/fugates-secrets/sap-dev-basic.conf
echo "SEGREDO_REMOVIDO:$(test -f /etc/nginx/fugates-secrets/sap-dev-basic.conf && echo nao || echo sim)"
echo "REVERTIDO_PARA:$B"
sed -n '/location = \/api\/v1\/integracoes\/sap\/contratos/,/^    }/p' $C | sed -n 's/.*proxy_pass http:\/\/127.0.0.1:\([0-9]*\).*/DESTINO:\1/p' | head -1
'@
    RemotoInterativo $reverterScript 'r167_reverter.sh'
    if ($LASTEXITCODE -ne 0) { Abortar 'REVERSAO' 'nao foi possivel reverter; confira a VM antes de novo envio do SAP' }
    Veredito 'REVERSAO' 'PASS' 'ingresso devolvido ao destino HML original'
    Resumo; exit 0
}

# ---------------------------------------------------------------- corte
if (-not $InjetarBasic) {
    Abortar 'AUTENTICACAO' @'
o location atual remove o header Authorization, e o receptor DEV exige Basic: sem -InjetarBasic o
corte produziria 401 garantido. Use -InjetarBasic apenas sabendo que a credencial injetada
autentica o INGRESSO, nao o emissor SAP.
'@
}
if (-not $devNoAr) { Abortar 'RECEPTOR_DEV' "nada escutando em 127.0.0.1:$PortaDev; instale o receptor antes do corte" }

$cortarScript = @'
set -eu
C=/etc/nginx/sites-available/fugates-hml-http.conf
PORTA="$1"
test -f /home/michael/.fugates-sap-inbound-receiver/basic.env
U=$(sed -n 's/^SapInbound__BasicAuth__Username=//p' /home/michael/.fugates-sap-inbound-receiver/basic.env)
S=$(sed -n 's/^SapInbound__BasicAuth__Password=//p' /home/michael/.fugates-sap-inbound-receiver/basic.env)
test -n "$U" && test -n "$S"
B64=$(printf '%s:%s' "$U" "$S" | base64 -w0)
BK=$C.backup.r167.$(date -u +%Y%m%d_%H%M%SZ)
sudo -p 'Senha sudo do DEV: ' cp -p "$C" "$BK"
# O arquivo de sites-available e 644 em diretorio 755: qualquer usuario local o le. A credencial
# NAO pode morar ali. Ela vai para /etc/nginx/fugates-secrets (750 root:www-data), que o nginx le
# como www-data e os demais usuarios nao alcancam. O conf publico guarda apenas o include.
SEC=/etc/nginx/fugates-secrets/sap-dev-basic.conf
umask 077
printf 'proxy_set_header Authorization "Basic %s";\n' "$B64" > /tmp/r167_sec.conf
sudo install -o root -g www-data -m 640 /tmp/r167_sec.conf "$SEC"
shred -u /tmp/r167_sec.conf 2>/dev/null || rm -f /tmp/r167_sec.conf
unset B64 S
# Troca APENAS dentro do location do contrato SAP: destino e credencial de canal.
sudo awk -v porta="$PORTA" -v sec="$SEC" '
  /location = \/api\/v1\/integracoes\/sap\/contratos/ { dentro=1 }
  dentro && /proxy_pass http:\/\/127\.0\.0\.1:8080;/ { sub(/8080/, porta) }
  # O comentario original dizia que nao ha Authorization neste trecho. Com o corte isso deixa de
  # ser verdade, e a configuracao tem de documentar o que realmente faz.
  dentro && /# No external Authorization contract in internal HML\./ {
    print "        # R167: credencial de CANAL entre este ingresso e o receptor DEV.";
    print "        # Autentica o INGRESSO, nao o emissor SAP: a identidade do remetente continua";
    print "        # sendo a allowlist de IP acima. Nao e credencial do SAP e nao foi entregue a ele.";
    print "        # O valor fica em " sec " (640 root:www-data), fora deste arquivo, que e 644.";
    next
  }
  dentro && /proxy_set_header Authorization ""/ { print "        include " sec ";"; next }
  dentro && /^    }/ { dentro=0 }
  { print }
' "$BK" > /tmp/r167_novo.conf
sudo cp /tmp/r167_novo.conf "$C"
sudo rm -f /tmp/r167_novo.conf
sudo nginx -t
sudo systemctl reload nginx
echo "BACKUP:$BK"
sed -n '/location = \/api\/v1\/integracoes\/sap\/contratos/,/^    }/p' $C | sed -n 's/.*proxy_pass http:\/\/127.0.0.1:\([0-9]*\).*/DESTINO:\1/p' | head -1
echo "AUTH_VIA_INCLUDE:$(sed -n '/location = \/api\/v1\/integracoes\/sap\/contratos/,/^    }/p' $C | grep -c "include $SEC;")"
echo "SEGREDO_NO_CONF_PUBLICO:$(grep -c 'Authorization \"Basic' $C)"
echo "SEGREDO_PROTEGIDO:$(stat -c %a:%U:%G $SEC)"
echo "OUTRAS_LOCATIONS_INTACTAS:$(grep -c 'proxy_pass http://127.0.0.1:18086' $C)"
'@
RemotoInterativo $cortarScript 'r167_cortar.sh' @($PortaDev)
if ($LASTEXITCODE -ne 0) { Abortar 'CORTE' 'o corte falhou; o nginx nao foi recarregado com configuracao invalida (nginx -t barra antes)' }
Veredito 'CORTE' 'PASS' ("ingresso agora entrega em 127.0.0.1:{0}; backup gravado para reversao" -f $PortaDev)

Resumo
Write-Host ''
Write-Host 'JANELA ABERTA. Enquanto durar, os contratos SAP desta URL vao para o DEV, nao para o HML.' -ForegroundColor Red
Write-Host 'Reverta assim que o contrato de homologacao for recebido:' -ForegroundColor Yellow
Write-Host '  .\Cortar-IngressoSapParaDev.ps1 -Reverter -Confirmo' -ForegroundColor Yellow
