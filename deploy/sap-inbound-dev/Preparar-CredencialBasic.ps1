<#
    R166 - Credencial Basic do receptor SAP DEV.

    O segredo e gerado NA VM, gravado em arquivo modo 600 do proprio usuario, e NUNCA e impresso,
    versionado, copiado para o Windows nem transportado por este repositorio. Este script devolve
    apenas a IMPRESSAO DIGITAL (SHA-256) do segredo, que serve para conferir, fora de banda, que o
    emissor SAP/CPI recebeu exatamente o mesmo valor - sem que o valor apareca em lugar nenhum.

    O receptor le a credencial pelo AMBIENTE do processo (SapInbound__BasicAuth__Username e
    SapInbound__BasicAuth__Password), nao pelo appsettings. Sem as duas variaveis,
    SapContractInboundProductiveComposition recusa compor e o receptor nao sobe.

    Credencial de CANAL entre o Nginx e o receptor DEV: autentica o INGRESSO, nao o emissor SAP.
    NAO deve ser entregue ao SAP/CPI. A identidade do remetente segue sendo a allowlist de IP.

    Uso:
      .\Preparar-CredencialBasic.ps1 -Usuario <usuario acordado com o SAP/CPI>
      .\Preparar-CredencialBasic.ps1 -Conferir      # so mostra o estado atual, nada e gerado
#>
[CmdletBinding()]
param(
    [string]$Usuario,
    [switch]$Conferir,
    [switch]$Rotacionar,
    [string]$Endpoint = 'michael@192.168.17.40',
    [string]$IdentityFile = 'C:\ProgramData\FugaTES\agentic\ssh\fugates-agentic-nt-ti-256',
    [string]$KnownHosts = 'C:\Users\TI\.ssh\known_hosts'
)

$ErrorActionPreference = 'Stop'
$ssh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$lote = @('-F','NUL','-o','IdentitiesOnly=yes','-o','IdentityAgent=none','-o','StrictHostKeyChecking=yes',
          '-o',"UserKnownHostsFile=$KnownHosts",'-o','GlobalKnownHostsFile=NUL','-o','ConnectTimeout=20',
          '-o','BatchMode=yes','-i',$IdentityFile)

function ParaLf([string]$texto) { $texto -replace "`r`n", "`n" }
function Base64([string]$script) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ParaLf $script))) }
# -n: nunca encaminha a entrada padrao local, para que nenhum prompt remoto ecoe segredo.
function Remoto([string]$script, [string[]]$parametros = @()) {
    $comando = 'printf %s ' + (Base64 $script) + ' | base64 -d > /tmp/r166_basic.sh; bash /tmp/r166_basic.sh'
    if ($parametros -and $parametros.Count -gt 0) { $comando += ' ' + ($parametros -join ' ') }
    & $ssh @($lote + @('-n') + @($Endpoint) + @($comando))
}

Write-Host '=== R166 - credencial Basic do receptor SAP DEV ===' -ForegroundColor Cyan
Write-Host ''

if ($Conferir) {
    $conferirScript = @'
set -eu
A=/home/michael/.fugates-sap-inbound-receiver/basic.env
if [ ! -f "$A" ]; then echo "ESTADO:ausente"; exit 0; fi
echo "ESTADO:presente"
echo "MODO:$(stat -c %a "$A")"
echo "DONO:$(stat -c %U "$A")"
# Apenas o NOME das variaveis e a impressao digital do segredo. Nunca o valor.
echo "VARIAVEIS:$(sed -n 's/^\([A-Za-z_]*\)=.*/\1/p' "$A" | tr '\n' ',')"
echo "USUARIO:$(sed -n 's/^SapInbound__BasicAuth__Username=//p' "$A")"
echo "DIGITAL:$(sed -n 's/^SapInbound__BasicAuth__Password=//p' "$A" | tr -d '\n' | sha256sum | cut -c1-32)"
'@
    $r = @(Remoto $conferirScript)
    if ($LASTEXITCODE -ne 0) { Write-Host 'BLOCKED  conferencia remota falhou' -ForegroundColor Red; exit 1 }
    $r | ForEach-Object { Write-Host "  $_" }
    Write-Host ''
    Write-Host 'O usuario aparece porque nao e segredo. A senha aparece apenas como impressao digital.' -ForegroundColor Yellow
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Usuario)) {
    Write-Host 'BLOCKED  informe -Usuario: o nome de usuario Basic e acordado com o emissor SAP/CPI' -ForegroundColor Red
    Write-Host '         (nao invento este valor; ele vem da definicao empresarial)'
    exit 1
}
if ($Usuario -notmatch '^[A-Za-z0-9_.-]{3,64}$') {
    Write-Host 'BLOCKED  usuario invalido: use 3 a 64 caracteres [A-Za-z0-9_.-]' -ForegroundColor Red
    exit 1
}

# O segredo nasce e morre na VM. O Windows nunca o ve; este script nunca o recebe.
$gerarScript = @'
set -eu
USUARIO="$1"; ROTACIONAR="$2"
D=/home/michael/.fugates-sap-inbound-receiver
A=$D/basic.env
umask 077
mkdir -p $D
if [ -f "$A" ] && [ "$ROTACIONAR" != "sim" ]; then
  echo "ESTADO:ja_existe"
  echo "DIGITAL:$(sed -n 's/^SapInbound__BasicAuth__Password=//p' "$A" | tr -d '\n' | sha256sum | cut -c1-32)"
  echo "AVISO:use -Rotacionar para substituir; a rotacao exige reacordo com o emissor SAP/CPI"
  exit 0
fi
if [ -f "$A" ]; then cp -p "$A" "$A.$(date -u +%Y%m%d_%H%M%SZ).bak"; fi
S="$(openssl rand -base64 48 | tr -d '\n/:@' | cut -c1-48)"
printf 'SapInbound__BasicAuth__Username=%s\n' "$USUARIO" > "$A"
printf 'SapInbound__BasicAuth__Password=%s\n' "$S" >> "$A"
chmod 600 "$A"
echo "ESTADO:gerado"
echo "MODO:$(stat -c %a "$A")"
echo "USUARIO:$USUARIO"
echo "DIGITAL:$(printf %s "$S" | sha256sum | cut -c1-32)"
unset S
'@
$rot = if ($Rotacionar) { 'sim' } else { 'nao' }
$resultado = @(Remoto $gerarScript @($Usuario, $rot))
if ($LASTEXITCODE -ne 0) { Write-Host 'BLOCKED  geracao remota falhou' -ForegroundColor Red; exit 1 }
$resultado | ForEach-Object { Write-Host "  $_" }

Write-Host ''
Write-Host 'O segredo esta apenas em /home/michael/.fugates-sap-inbound-receiver/basic.env (600) na VM.' -ForegroundColor Green
Write-Host 'Nao foi impresso aqui, nao esta no repositorio e nao passou pelo Windows.' -ForegroundColor Green
Write-Host ''
Write-Host 'USO: esta credencial e de CANAL, entre o ingresso Nginx e o receptor DEV. Ela autentica' -ForegroundColor Yellow
Write-Host 'o ingresso, NAO o emissor SAP, e por isso NAO deve ser entregue ao SAP/CPI nem a nenhum' -ForegroundColor Yellow
Write-Host 'terceiro. A identidade do remetente continua sendo a allowlist de IP do Nginx.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'A impressao digital acima serve apenas para conferir, sem expor o valor, que o Nginx e o' -ForegroundColor Yellow
Write-Host 'receptor estao usando o mesmo segredo.' -ForegroundColor Yellow
