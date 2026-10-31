<#
    R166 - Aplica o 007: SELECT das tabelas de recebimento Toledo para a identidade GET-only
    fugates_dev_read_app, que serve a API privada 18762.

    POR QUE: o 005 concedeu SELECT numa lista fixa, anterior as tabelas Toledo. Com a API promovida
    para a identidade GET-only, /dev/read/toledo passou a devolver 503 UNIFIED_DEV_READ_UNAVAILABLE.

    ESCOPO: somente SELECT, somente nas 4 tabelas que a consulta usa, mais 2 colunas de integracao.
    Nada de escrita, nada para PUBLIC, nada para a role de escrita do receptor SAP, e a role
    compartilhada nao e tocada. O 007 e transacional, com guardas e postcheck proprios: se algo
    divergir, nada e commitado.

    A senha do sudo e digitada NESTA janela, na sessao SSH. Este script nao a solicita no chat,
    nao a captura e nao a registra.

    Uso:
      powershell -NoProfile -ExecutionPolicy Bypass -File .\Aplicar-SelectToledoDev.ps1
      .\Aplicar-SelectToledoDev.ps1 -Conferir     # somente leitura: mostra o estado atual
#>
[CmdletBinding()]
param(
    [switch]$Conferir,
    [string]$Endpoint = 'michael@192.168.17.40',
    [string]$IdentityFile = 'C:\ProgramData\FugaTES\agentic\ssh\fugates-agentic-nt-ti-256',
    [string]$KnownHosts = 'C:\Users\TI\.ssh\known_hosts'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sql  = Join-Path $repo 'docs\Incrementos\UnifiedDev\007_DEV_READ_ROLE_TOLEDO_SELECT.sql'
$ssh  = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$scp  = Join-Path $env:SystemRoot 'System32\OpenSSH\scp.exe'
$base = @('-F','NUL','-o','IdentitiesOnly=yes','-o','IdentityAgent=none','-o','StrictHostKeyChecking=yes',
          '-o',"UserKnownHostsFile=$KnownHosts",'-o','GlobalKnownHostsFile=NUL','-o','ConnectTimeout=20','-i',$IdentityFile)
$lote = $base + @('-o','BatchMode=yes')

$estado = [ordered]@{}
function Veredito([string]$chave, [string]$valor, [string]$detalhe = '') {
    $estado[$chave] = $valor
    $cor = if ($valor -like 'PASS*') { 'Green' } elseif ($valor -like 'BLOCKED*') { 'Red' } else { 'Yellow' }
    Write-Host ("{0,-26} {1}" -f $chave, $valor) -ForegroundColor $cor
    if ($detalhe) { Write-Host ("  " + $detalhe) }
}
function Resumo { Write-Host ''; Write-Host '=== RESUMO ===' -ForegroundColor Cyan
    $estado.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-26} {1}" -f $_.Key, $_.Value) } }
function Abortar([string]$chave, [string]$motivo) { Veredito $chave 'BLOCKED' $motivo; Resumo; exit 1 }

function ParaLf([string]$t) { $t -replace "`r`n", "`n" }
function Base64([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ParaLf $s))) }
# -n: nunca encaminha a entrada padrao local; um prompt remoto jamais ecoa segredo nem trava.
function Remoto([string]$script, [string[]]$parametros = @()) {
    $comando = 'printf %s ' + (Base64 $script) + ' | base64 -d > /tmp/r166_sel.sh; bash /tmp/r166_sel.sh'
    if ($parametros -and $parametros.Count -gt 0) { $comando += ' ' + ($parametros -join ' ') }
    & $ssh @($lote + @('-n') + @($Endpoint) + @($comando))
}
# Grava o script na VM sem TTY e o executa COM TTY, para o sudo pedir a senha nesta janela.
function RemotoInterativo([string]$script, [string]$nome, [string[]]$parametros = @()) {
    $instalar = 'umask 077; printf %s ' + (Base64 $script) + " | base64 -d > /tmp/$nome; chmod 700 /tmp/$nome"
    & $ssh @($lote + @('-n') + @($Endpoint) + @($instalar)) | Out-Null
    if ($LASTEXITCODE -ne 0) { Abortar 'ROTEIRO_REMOTO' "nao foi possivel gravar /tmp/$nome na VM" }
    & $ssh @($base + @('-tt') + @($Endpoint) + @("bash /tmp/$nome " + ($parametros -join ' ')))
}

Write-Host '=== R166 - SELECT Toledo para a identidade GET-only ===' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------- alvo e integridade
$alvoScript = @'
set -eu
hostname
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tA -f - <<'SQL'
select current_database() || ';' || current_setting('port') || ';' || current_setting('cluster_name')
       || ';' || pg_is_in_recovery()::text;
SQL
'@
$alvo = @(Remoto $alvoScript)
if ($LASTEXITCODE -ne 0 -or $alvo.Count -lt 2) { Abortar 'ALVO' "preflight remoto falhou: $($alvo -join ' / ')" }
if ($alvo[0] -ne 'vm-fugatesbio') { Abortar 'ALVO' "host inesperado: $($alvo[0])" }
if ($alvo[1] -ne 'fugates_dev;5433;18/fugatesacmdev;false') { Abortar 'ALVO' "identidade do banco inesperada: $($alvo[1])" }
Veredito 'ALVO' 'PASS' ("{0} | {1}" -f $alvo[0], $alvo[1])

if (-not (Test-Path $sql)) { Abortar 'SQL_INTEGRIDADE' "ausente: $sql" }
$sha = (Get-FileHash $sql -Algorithm SHA256).Hash
Veredito 'SQL_INTEGRIDADE' 'PASS' ("007={0}" -f $sha.Substring(0, 16))

# ---------------------------------------------------------------- estado atual
$estadoScript = @'
set -eu
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tA -f - <<'SQL'
select string_agg(v.obj || '=' || has_table_privilege('fugates_dev_read_app', v.obj, 'SELECT')::text, ' | ' order by v.obj)
  from (values ('fugates.toledo_pesagem_recebida'),('fugates.toledo_pesagem_item'),
               ('fugates.tentativa_integracao'),('fugates.auditoria_integracao')) v(obj);
SQL
'@
$antes = (@(Remoto $estadoScript | Where-Object { $_ -match '=' }) | Select-Object -First 1)
Veredito 'ANTES' 'CONFERENCIA' $antes

if ($Conferir) { Resumo; exit 0 }
if ($antes -notmatch 'false') { Veredito 'SELECT_TOLEDO' 'PASS' 'ja concedido; nada a fazer'; Resumo; exit 0 }

# ---------------------------------------------------------------- aplicacao
Write-Host ''
Write-Host 'Aplicando o 007. A senha do sudo e pedida NESTA janela.' -ForegroundColor Yellow
& $scp @lote -- $sql "${Endpoint}:/tmp/r166_007.sql" | Out-Null
if ($LASTEXITCODE -ne 0) { Abortar 'SELECT_TOLEDO' 'envio do 007 falhou; nada foi alterado' }

$aplicar = @'
set -eu
SQL="$1"; ESPERADO="$2"
test "$(sha256sum "$SQL" | cut -d" " -f1 | tr a-f A-F)" = "$ESPERADO"
sudo -p 'Senha sudo do DEV: ' -u postgres psql -p 5433 -d fugates_dev -v ON_ERROR_STOP=1 -P pager=off -f "$SQL"
'@
RemotoInterativo $aplicar 'r166_007.sh' @('/tmp/r166_007.sql', $sha)
if ($LASTEXITCODE -ne 0) { Abortar 'SELECT_TOLEDO' '007 falhou; e transacional, nada foi commitado. Rollback: 007_..._ROLLBACK.sql' }

$depois = (@(Remoto $estadoScript | Where-Object { $_ -match '=' }) | Select-Object -First 1)
if ($depois -match 'false') { Abortar 'SELECT_TOLEDO' "o GRANT nao ficou efetivo: $depois" }
Veredito 'SELECT_TOLEDO' 'PASS' $depois

# ---------------------------------------------------------------- prova de ponta a ponta
$provaScript = @'
set -eu
K=$(cat /home/michael/.fugates-r165al-physical-R2/run/READ_API_KEY)
printf 'identity=%s ' "$(curl -s -o /dev/null -w %{http_code} -H "X-R165AL-Read-Key: $K" http://127.0.0.1:18762/dev/read/identity)"
printf 'contracts=%s ' "$(curl -s -o /dev/null -w %{http_code} -H "X-R165AL-Read-Key: $K" http://127.0.0.1:18762/dev/read/contracts)"
printf 'toledo=%s\n'   "$(curl -s -o /dev/null -w %{http_code} -H "X-R165AL-Read-Key: $K" http://127.0.0.1:18762/dev/read/toledo)"
'@
$prova = (@(Remoto $provaScript | Where-Object { $_ -match 'toledo=' }) | Select-Object -First 1)
if ($prova -notmatch 'identity=200' -or $prova -notmatch 'contracts=200' -or $prova -notmatch 'toledo=200') {
    Abortar 'API_18762' "a API nao respondeu como esperado: $prova"
}
Veredito 'API_18762' 'PASS' $prova

# A identidade de escrita do receptor SAP nao pode ter ganho leitura Toledo.
$isolamentoScript = @'
set -eu
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tA -f - <<'SQL'
select 'writer_toledo=' || has_table_privilege('fugates_sap_inbound_app','fugates.toledo_pesagem_recebida','SELECT')::text
       || ' | shared_insert=' || has_table_privilege('fugates_acm_hml_app','fugates.integracao','INSERT')::text
       || ' | read_writes=' || (select count(*) from pg_class c join pg_namespace n on n.oid=c.relnamespace
            cross join (values ('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE')) v(a)
            where c.relkind in ('r','p') and n.nspname in ('fugates','masterdata')
              and has_table_privilege('fugates_dev_read_app', c.oid, v.a))::text;
SQL
'@
$iso = (@(Remoto $isolamentoScript | Where-Object { $_ -match 'writer_toledo=' }) | Select-Object -First 1)
if ($iso -notmatch 'writer_toledo=false' -or $iso -notmatch 'shared_insert=true' -or $iso -notmatch 'read_writes=0') {
    Abortar 'ISOLAMENTO' "isolamento das identidades divergente: $iso"
}
Veredito 'ISOLAMENTO' 'PASS' $iso

Resumo
Write-Host ''
Write-Host 'Nenhuma escrita foi concedida, a role de escrita do receptor nao foi afetada e a' -ForegroundColor Green
Write-Host 'role compartilhada segue intacta. Janela R57 fechada; nenhum POST executado.' -ForegroundColor Green
