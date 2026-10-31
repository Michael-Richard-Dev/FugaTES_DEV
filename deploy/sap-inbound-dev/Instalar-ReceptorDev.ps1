<#
    R166 - Instalacao ISOLADA do receptor SAP DEV. NAO ativa rota corporativa, NAO abre janela R57,
    NAO executa POST SAP ou Guardian, NAO reinicia 18761/18762/18763 e NAO toca o ingress HML 8080.

    O receptor sobe numa porta loopback propria, aprovada pela Hera, em diretorio proprio.
    Enquanto houver qualquer <<PENDENTE...>> na configuracao, o processo RECUSA subir por desenho
    (SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED) - e este script para antes de tentar.

    Fases:
      0  confere alvo (host, usuario) e que as portas em uso nao serao tocadas
      1  confere SHA do pacote e ausencia de pendencias na configuracao
      2  confere que a identidade de escrita autentica e conecta no fugates_dev:5433
      3  faz backup da instalacao anterior (se houver) e extrai a nova em diretorio datado
      4  sobe o processo e aguarda o listener na porta aprovada
      5  confere /health e que NENHUMA rota de callback/Toledo foi exposta
      6  em qualquer falha: encerra o processo novo, restaura o anterior e devolve BLOCKED

    Uso:
      powershell -NoProfile -ExecutionPolicy Bypass -File .\Instalar-ReceptorDev.ps1 `
          -Pacote <caminho\FUGATES_SAP_INBOUND_RECEIVER_DEV.zip> -Porta <porta aprovada>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Pacote,
    [Parameter(Mandatory = $true)][int]$Porta,
    [string]$ShaPacote = '1FEB21CCB452EB7D5766C17E76FAB4B9DF37DF382A2D6068D0BB795340A73C2F',
    [string]$Endpoint = 'michael@192.168.17.40',
    [string]$IdentityFile = 'C:\ProgramData\FugaTES\agentic\ssh\fugates-agentic-nt-ti-256',
    [string]$KnownHosts = 'C:\Users\TI\.ssh\known_hosts',
    [switch]$Conferir
)

$ErrorActionPreference = 'Stop'
$ssh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$scp = Join-Path $env:SystemRoot 'System32\OpenSSH\scp.exe'
$base = @('-F','NUL','-o','IdentitiesOnly=yes','-o','IdentityAgent=none','-o','StrictHostKeyChecking=yes',
          '-o',"UserKnownHostsFile=$KnownHosts",'-o','GlobalKnownHostsFile=NUL','-o','ConnectTimeout=20','-i',$IdentityFile)
$lote = $base + @('-o','BatchMode=yes')
$raiz = '/home/michael/.fugates-sap-inbound-receiver'

$estado = [ordered]@{}
function Veredito([string]$chave, [string]$valor, [string]$detalhe = '') {
    $estado[$chave] = $valor
    $cor = if ($valor -like 'PASS*') { 'Green' } elseif ($valor -like 'BLOCKED*') { 'Red' } else { 'Yellow' }
    Write-Host ("{0,-28} {1}" -f $chave, $valor) -ForegroundColor $cor
    if ($detalhe) { Write-Host ("  " + $detalhe) }
}
function Resumo {
    Write-Host ''; Write-Host '=== RESUMO ===' -ForegroundColor Cyan
    $estado.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-28} {1}" -f $_.Key, $_.Value) }
    Write-Host '  SAP_RECEIVER_ACTIVE          NO (rota corporativa nao publicada)'
    Write-Host '  R57_WINDOW                   CLOSED'
    Write-Host '  REAL_SAP_POSTS               0'
    Write-Host '  REAL_GUARDIAN_POSTS          0'
}
function Abortar([string]$chave, [string]$motivo) { Veredito $chave 'BLOCKED' $motivo; Resumo; exit 1 }

function ParaLf([string]$texto) { $texto -replace "`r`n", "`n" }
function Base64([string]$script) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ParaLf $script))) }
# -n: nunca encaminha a entrada padrao local, para que nenhum prompt remoto ecoe segredo nem trave.
function Remoto([string]$script, [string[]]$parametros = @()) {
    $comando = 'printf %s ' + (Base64 $script) + ' | base64 -d > /tmp/r166_recv.sh; bash /tmp/r166_recv.sh'
    if ($parametros -and $parametros.Count -gt 0) { $comando += ' ' + ($parametros -join ' ') }
    & $ssh @($lote + @('-n') + @($Endpoint) + @($comando))
}

Write-Host '=== R166 - receptor SAP DEV (instalacao isolada) ===' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------- FASE 0
if ($Porta -lt 1024 -or $Porta -gt 65535) { Abortar 'ALVO' "porta fora da faixa: $Porta" }
foreach ($reservada in @(8080, 18761, 18762, 18763, 5433, 5432, 5434)) {
    if ($Porta -eq $reservada) { Abortar 'ALVO' "porta $Porta pertence a um servico existente e NAO pode ser reutilizada" }
}
$alvoScript = @'
set -eu
hostname; id -un
for p in 8080 18761 18762 18763; do
  ss -ltn | grep -q "127.0.0.1:$p" && echo "EMUSO:$p" || echo "AUSENTE:$p"
done
ss -ltn | grep -q "127.0.0.1:__PORTA__" && echo "DESTINO_OCUPADO" || echo "DESTINO_LIVRE"
'@
$alvo = @(Remoto ($alvoScript -replace '__PORTA__', $Porta))
if ($LASTEXITCODE -ne 0 -or $alvo.Count -lt 3) { Abortar 'ALVO' "preflight remoto falhou: $($alvo -join ' / ')" }
if ($alvo[0] -ne 'vm-fugatesbio') { Abortar 'ALVO' "host inesperado: $($alvo[0])" }
if ($alvo[1] -ne 'michael')       { Abortar 'ALVO' "usuario inesperado: $($alvo[1])" }
if ($alvo -notcontains 'DESTINO_LIVRE') { Abortar 'ALVO' "a porta $Porta ja esta em uso na VM" }
$preservados = @($alvo | Where-Object { $_ -like 'EMUSO:*' })
Veredito 'ALVO' 'PASS' ("vm-fugatesbio;michael | destino {0} livre | preservados: {1}" -f $Porta, ($preservados -join ','))

# ---------------------------------------------------------------- FASE 1
if (-not (Test-Path $Pacote)) { Abortar 'PACOTE' "ausente: $Pacote" }
$sha = (Get-FileHash $Pacote -Algorithm SHA256).Hash
if ($sha -ne $ShaPacote.ToUpper()) { Abortar 'PACOTE' "SHA difere do aprovado`n  esperado: $ShaPacote`n  obtido  : $sha" }
Veredito 'PACOTE' 'PASS' $sha.Substring(0, 16)

# A configuracao TEM que estar completa: enquanto houver pendencia, o receptor nao sobe.
# Expand-Archive e nativo do Windows PowerShell 5.1; [IO.Compression.ZipFile] exigiria Add-Type.
$temporario = Join-Path $env:TEMP ('r166_cfg_' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -Path $Pacote -DestinationPath $temporario -Force
    $arquivoConfig = Join-Path $temporario 'appsettings.receiver.dev.json'
    if (-not (Test-Path $arquivoConfig)) { Abortar 'CONFIGURACAO' 'appsettings.receiver.dev.json ausente do pacote' }
    $conteudo = Get-Content $arquivoConfig -Raw
} finally {
    if (Test-Path $temporario) { Remove-Item $temporario -Recurse -Force -ErrorAction SilentlyContinue }
}

$pendentes = ([regex]'<<PENDENTE[A-Z_0-9]*>>').Matches($conteudo) | ForEach-Object { $_.Value } | Select-Object -Unique
if ($pendentes.Count -gt 0) {
    Abortar 'CONFIGURACAO' ("a configuracao tem {0} definicao(oes) empresarial(is) pendente(s); o receptor recusaria subir:`n  {1}" -f $pendentes.Count, ($pendentes -join "`n  "))
}
if ($conteudo -notmatch 'fugates_sap_inbound_app') { Abortar 'CONFIGURACAO' 'a identidade de escrita nao esta declarada' }
if ($conteudo -match '"ToledoInbound"')            { Abortar 'CONFIGURACAO' 'rota Toledo nao pertence a este receptor' }
if ($conteudo -notmatch '"Callback"\s*:\s*\{\s*"Enabled"\s*:\s*false') { Abortar 'CONFIGURACAO' 'Callback Guardian precisa estar desabilitado' }
Veredito 'CONFIGURACAO' 'PASS' 'sem pendencias; escrita declarada; callback e Toledo desabilitados'

# ---------------------------------------------------------------- FASE 2
$authScript = @'
set -eu
test -f /home/michael/.fugates-sap-inbound/pgpass
test "$(stat -c %a /home/michael/.fugates-sap-inbound/pgpass)" = 600
PGPASSFILE=/home/michael/.fugates-sap-inbound/pgpass psql -w \
  'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_sap_inbound_app' -tA -f - <<'SQL'
select current_user || ';' || current_database() || ';' || current_setting('port')
       || ';' || current_setting('cluster_name');
SQL
'@
$auth = (@(Remoto $authScript | Where-Object { $_ -match ';' }) | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or $auth -ne 'fugates_sap_inbound_app;fugates_dev;5433;18/fugatesacmdev') {
    Abortar 'SAP_WRITER_AUTH' "autenticacao/destino divergente: $auth"
}
Veredito 'SAP_WRITER_AUTH' 'PASS' $auth

# A credencial Basic NAO vem do appsettings: entra pelo ambiente do processo, de um arquivo 600 na
# propria VM. Aqui se confere existencia, modo e presenca das duas variaveis - nunca o valor.
$basicScript = @'
set -eu
A=/home/michael/.fugates-sap-inbound-receiver/basic.env
test -f "$A" || { echo "AUSENTE"; exit 1; }
test "$(stat -c %a "$A")" = 600 || { echo "MODO_INSEGURO:$(stat -c %a "$A")"; exit 1; }
grep -q '^SapInbound__BasicAuth__Username=.\+' "$A" || { echo "USUARIO_AUSENTE"; exit 1; }
grep -q '^SapInbound__BasicAuth__Password=.\+' "$A" || { echo "SENHA_AUSENTE"; exit 1; }
echo "OK:$(sed -n 's/^SapInbound__BasicAuth__Password=//p' "$A" | tr -d '\n' | sha256sum | cut -c1-16)"
'@
$basic = @(Remoto $basicScript | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or $basic -notlike 'OK:*') {
    Abortar 'BASIC_CREDENTIAL' "credencial Basic indisponivel ou insegura ($basic); prepare-a com Preparar-CredencialBasic.ps1"
}
Veredito 'BASIC_CREDENTIAL' 'PASS' ("presente, modo 600, impressao digital {0}" -f ($basic -replace '^OK:', ''))

if ($Conferir) { Veredito 'INSTALACAO' 'CONFERENCIA' 'modo -Conferir: nada foi instalado'; Resumo; exit 0 }

# ---------------------------------------------------------------- FASE 3
& $scp @lote -- $Pacote "${Endpoint}:$raiz/$sha.zip" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    # Primeira instalacao: o diretorio raiz ainda nao existe.
    Remoto "set -eu`nmkdir -m 700 -p $raiz" | Out-Null
    & $scp @lote -- $Pacote "${Endpoint}:$raiz/$sha.zip" | Out-Null
    if ($LASTEXITCODE -ne 0) { Abortar 'INSTALACAO' 'envio do pacote falhou; nada foi alterado' }
}

$instalarScript = @'
set -eu
RAIZ="$1"; SHA="$2"; PORTA="$3"
test "$(sha256sum $RAIZ/$SHA.zip | cut -d" " -f1 | tr a-f A-F)" = "$SHA"
ANTERIOR=$(ls -1dt $RAIZ/*_RECEPTOR/ 2>/dev/null | head -1 || true)
if [ -n "${ANTERIOR:-}" ]; then
  tar -czf $RAIZ/BACKUP_RECEPTOR_$(date -u +%Y%m%d_%H%M%SZ).tar.gz -C $RAIZ "$(basename $ANTERIOR)"
  echo "BACKUP_FEITO:$ANTERIOR"
else
  echo "BACKUP_DISPENSADO:primeira_instalacao"
fi
NOVO=$RAIZ/$(date -u +%Y%m%d_%H%M%S)_RECEPTOR
mkdir -m 700 $NOVO
python3 -c 'import zipfile,sys;zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' $RAIZ/$SHA.zip $NOVO
echo "EXTRAIDO:$NOVO"
'@
$instalacao = @(Remoto $instalarScript @($raiz, $sha, $Porta))
if ($LASTEXITCODE -ne 0) { Abortar 'INSTALACAO' "extracao falhou: $($instalacao -join ' / ')" }
$novo = ($instalacao | Where-Object { $_ -like 'EXTRAIDO:*' } | Select-Object -First 1) -replace '^EXTRAIDO:', ''
$anterior = ($instalacao | Where-Object { $_ -like 'BACKUP_FEITO:*' } | Select-Object -First 1) -replace '^BACKUP_FEITO:', ''
if (-not $novo) { Abortar 'INSTALACAO' 'diretorio novo nao identificado' }
Veredito 'INSTALACAO' 'PASS' $novo

# ---------------------------------------------------------------- FASES 4 e 5
$subirScript = @'
set -eu
NOVO="$1"; PORTA="$2"
cd $NOVO
# A credencial Basic entra pelo ambiente, lida do arquivo 600; nunca aparece em linha de comando,
# em log ou no appsettings. set -a exporta o que o arquivo definir, e so isso.
set -a
. /home/michael/.fugates-sap-inbound-receiver/basic.env
set +a
ASPNETCORE_URLS="http://127.0.0.1:$PORTA" \
  nohup /usr/bin/dotnet $NOVO/FugaTES.Callback.Http.dll >> $NOVO/receptor.log 2>&1 &
echo $! > $NOVO/PID
for i in $(seq 1 40); do ss -ltn | grep -q "127.0.0.1:$PORTA" && break; sleep 0.5; done
if ! ss -ltn | grep -q "127.0.0.1:$PORTA"; then echo "LISTENER_AUSENTE"; tail -5 $NOVO/receptor.log; exit 1; fi
echo "HEALTH:$(curl -s -o /dev/null -w %{http_code} http://127.0.0.1:$PORTA/health || echo 000)"
# Nenhuma rota de callback Guardian nem Toledo pode responder neste receptor.
echo "CALLBACK:$(curl -s -o /dev/null -w %{http_code} -X POST http://127.0.0.1:$PORTA/callback/guardian || echo 000)"
echo "TOLEDO:$(curl -s -o /dev/null -w %{http_code} -X POST http://127.0.0.1:$PORTA/callback/toledo || echo 000)"
'@
$subida = @(Remoto $subirScript @($novo, $Porta))
$saude = ($subida | Where-Object { $_ -like 'HEALTH:*' } | Select-Object -First 1) -replace '^HEALTH:', ''
$callback = ($subida | Where-Object { $_ -like 'CALLBACK:*' } | Select-Object -First 1) -replace '^CALLBACK:', ''
$toledo = ($subida | Where-Object { $_ -like 'TOLEDO:*' } | Select-Object -First 1) -replace '^TOLEDO:', ''

$falha = $null
if ($LASTEXITCODE -ne 0 -or $saude -ne '200') { $falha = "listener ou /health invalido (health=$saude): $($subida -join ' / ')" }
elseif ($callback -ne '404') { $falha = "rota de callback Guardian respondeu $callback; esperado 404" }
elseif ($toledo -ne '404')   { $falha = "rota Toledo respondeu $toledo; esperado 404" }

if ($falha) {
    # ------------------------------------------------------------ FASE 6: rollback
    $rollbackScript = @'
set -eu
NOVO="$1"; PORTA="$2"; ANTERIOR="${3:-}"
kill $(cat $NOVO/PID 2>/dev/null) 2>/dev/null || true
for i in $(seq 1 20); do ss -ltn | grep -q "127.0.0.1:$PORTA" || break; sleep 0.5; done
mv $NOVO ${NOVO}_FALHOU
if [ -n "$ANTERIOR" ] && [ -d "$ANTERIOR" ]; then echo "ANTERIOR_PRESERVADO:$ANTERIOR"; else echo "SEM_ANTERIOR"; fi
ss -ltn | grep -q "127.0.0.1:$PORTA" && echo "PORTA_AINDA_ABERTA" || echo "PORTA_LIVRE"
'@
    $r = @(Remoto $rollbackScript @($novo, $Porta, $anterior))
    Veredito 'ROLLBACK' $(if ($r -contains 'PORTA_LIVRE') { 'PASS' } else { 'BLOCKED' }) ($r -join ' | ')
    Abortar 'RECEPTOR' $falha
}

Veredito 'RECEPTOR' 'PASS' "listener 127.0.0.1:$Porta | /health 200 | callback 404 | toledo 404"
Veredito 'SERVICOS_PRESERVADOS' 'PASS' '18761, 18762, 18763 e 8080 nao foram tocados'
Resumo
Write-Host ''
Write-Host 'Receptor instalado e INATIVO para o SAP: nenhuma rota corporativa publicada,' -ForegroundColor Yellow
Write-Host 'janela R57 fechada. A liberacao do ingress exige autorizacao especifica da Hera.' -ForegroundColor Yellow
