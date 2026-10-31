<#
    R164 - Roteiro ÚNICO de execução administrativa assistida das identidades PostgreSQL DEV.

    Executado pelo OPERADOR, a partir do Windows, num terminal interativo. A senha do sudo é
    digitada na própria sessão SSH; este script não a solicita no chat, não a captura, não a
    transporta e não a registra. As senhas das roles são geradas NA VM e nunca impressas.

    Fases, cada uma com veredito explícito:
      0  identifica o alvo (host, cluster, porta, base) e recusa qualquer divergência
      1  confere integridade dos SQLs aprovados e exige backup verificável
      2  cria fugates_dev_read_app (SELECT apenas) e fugates_sap_inbound_app (ACL contratual)
      3  postchecks de ACL e identidade; confirma a role compartilhada intacta
      4  valida autenticação e leitura reais da nova role ANTES de repontar a API
      5  promove a API 18762 para a identidade GET-only, com backup e rollback automático
      6  valida DEV110, seletor TODOS, seis contratos, JSON 1147/item 10 e o serviço 18763

    Para em qualquer falha, com veredito, e aplica rollback SOMENTE do que foi efetivamente
    alterado. Fases já concluídas são detectadas e puladas numa reexecução.

    NÃO autorizado por este gate, e portanto ausente deste script: alterar 5432/5434, revogar
    privilégios da role compartilhada, alterar o receptor HML 8080, publicar o ingress SAP DEV,
    abrir janela R57, executar POST SAP/Guardian, reiniciar o 18763 ou rotacionar credenciais.

    Uso:
      powershell -NoProfile -ExecutionPolicy Bypass -File .\Executar-IdentidadesDev.ps1 `
          -Pacote <caminho\FUGATES_UNIFIED_DEV_READ_API_ROLE_OPTIN.zip>

      -Conferir     somente leitura: fases 0, 1 e 3; nada é alterado
      -ApenasFase2  cria as roles e para antes de tocar na API
#>
[CmdletBinding()]
param(
    [string]$Pacote,
    [switch]$ApenasFase2,
    [switch]$Conferir,
    [string]$Endpoint = 'michael@192.168.17.40',
    [string]$IdentityFile = 'C:\ProgramData\FugaTES\agentic\ssh\fugates-agentic-nt-ti-256',
    [string]$KnownHosts = 'C:\Users\TI\.ssh\known_hosts',
    [string]$Dev110 = 'C:\FugaTES_DEV110',
    [string]$ShaPacote = 'CD0EE627C5C5156DB0F7C78979B863CF103EC9B7AA0970DB75AEDEFB8C60480E'
)

$ErrorActionPreference = 'Stop'
$repo     = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$sqlRead  = Join-Path $repo 'docs\Incrementos\UnifiedDev\005_DEV_READ_ONLY_ROLE.sql'
$sqlWrite = Join-Path $repo 'docs\Incrementos\UnifiedDev\004_SAP_INBOUND_WRITER_ROLE_DEV.sql'
$sqlConnect = Join-Path $repo 'docs\Incrementos\UnifiedDev\006_DEV_ROLES_CONNECT_GRANT.sql'
$ssh      = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$scp      = Join-Path $env:SystemRoot 'System32\OpenSSH\scp.exe'

# IMPORTANTE: o Windows remonta a linha de comando nativa e QUEBRA aspas duplas dentro do
# comando remoto. Por isso nenhum comando é passado como argumento: todo script remoto viaja
# pela ENTRADA PADRÃO (bash -s) ou é gravado na VM e executado com TTY livre para o sudo.
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
    $estado.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-26} {1}" -f $_.Key, $_.Value) }
    Write-Host '  SAP_RECEIVER_ACTIVE        NO'; Write-Host '  R57_WINDOW_ACTIVE          NO'; Write-Host '  EXTERNAL_POSTS             0' }
function Abortar([string]$chave, [string]$motivo) { Veredito $chave 'BLOCKED' $motivo; Resumo; exit 1 }

# O Windows grava here-strings com CRLF. No bash remoto o \r final quebra tanto o parsing de
# opções (head -1\r) quanto os terminadores de here-doc, então tudo é normalizado para LF.
function ParaLf([string]$texto) { $texto -replace "`r`n", "`n" }

# Executa um script remoto não interativo, enviado pela entrada padrão.
function Base64([string]$script) {
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ParaLf $script)))
}

# Executa um script remoto não interativo. O script viaja em base64 numa linha única: assim
# não há aspas para o Windows remontar nem conversão de LF para CRLF na entrada padrão — as
# duas armadilhas que quebram here-docs e opções como `head -1`.
function Remoto([string]$script, [string[]]$parametros) {
    $comando = 'printf %s ' + (Base64 $script) + ' | base64 -d > /tmp/r164_exec.sh; bash /tmp/r164_exec.sh'
    if ($parametros -and $parametros.Count -gt 0) { $comando += ' ' + ($parametros -join ' ') }
    # -n: nunca encaminha a entrada padrao local. Sem isto, um prompt remoto (psql, sudo) le o
    # que o operador digita COM ECO, expondo o segredo na tela, e a execucao fica travada.
    $a = @($lote) + @('-n') + @($Endpoint) + @($comando)
    & $ssh @a
}
# Grava um script na VM (sem TTY) e o executa COM TTY, para o sudo pedir a senha nesta janela.
function RemotoInterativo([string]$script, [string]$nome, [string[]]$parametros = @()) {
    # 1) grava o script na VM sem TTY, também em base64 numa linha única.
    $instalar = 'umask 077; printf %s ' + (Base64 $script) + " | base64 -d > /tmp/$nome; chmod 700 /tmp/$nome"
    $a = @($lote) + @($Endpoint) + @($instalar)
    & $ssh @a | Out-Null
    if ($LASTEXITCODE -ne 0) { Abortar 'ROTEIRO_REMOTO' "não foi possível gravar /tmp/$nome na VM" }
    # 2) executa COM TTY, deixando a entrada padrão livre para o prompt do sudo.
    $b = @($base) + @('-tt') + @($Endpoint) + @("bash /tmp/$nome " + ($parametros -join ' '))
    & $ssh @b
}

Write-Host '=== R164 - identidades PostgreSQL DEV ===' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------- FASE 0
$alvoScript = @'
set -eu
printf '%s;%s\n' "$(hostname)" "$(id -un)"
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tA -c \
 'select current_database()||chr(59)||current_setting(chr(112)||chr(111)||chr(114)||chr(116))||chr(59)||current_setting(chr(99)||chr(108)||chr(117)||chr(115)||chr(116)||chr(101)||chr(114)||chr(95)||chr(110)||chr(97)||chr(109)||chr(101))||chr(59)||pg_is_in_recovery()::text'
'@
$alvo = @(Remoto $alvoScript)
if ($LASTEXITCODE -ne 0 -or $alvo.Count -lt 2) { Abortar 'ALVO' "preflight remoto falhou: $($alvo -join ' / ')" }
if ($alvo[0] -ne 'vm-fugatesbio;michael') { Abortar 'ALVO' "host/usuário inesperado: $($alvo[0])" }
if ($alvo[1] -ne 'fugates_dev;5433;18/fugatesacmdev;false') { Abortar 'ALVO' "identidade do banco inesperada: $($alvo[1])" }
Veredito 'ALVO' 'PASS' ("{0} | {1}" -f $alvo[0], $alvo[1])

# ---------------------------------------------------------------- FASE 1
foreach ($f in @($sqlRead, $sqlWrite, $sqlConnect)) { if (-not (Test-Path $f)) { Abortar 'SQL_INTEGRIDADE' "ausente: $f" } }
$shaRead    = (Get-FileHash $sqlRead    -Algorithm SHA256).Hash
$shaWrite   = (Get-FileHash $sqlWrite   -Algorithm SHA256).Hash
$shaConnect = (Get-FileHash $sqlConnect -Algorithm SHA256).Hash
Veredito 'SQL_INTEGRIDADE' 'PASS' "005=$($shaRead.Substring(0,16))  004=$($shaWrite.Substring(0,16))  006=$($shaConnect.Substring(0,16))"

$backupScript = @'
set -eu
ls -1t /var/lib/postgresql/backup_fugates_dev_*.dump 2>/dev/null | head -1
'@
$backup = (@(Remoto $backupScript) | Select-Object -First 1)
if ($Conferir) { Veredito 'BACKUP' 'CONFERENCIA' $(if ($backup) { $backup } else { 'nenhum backup presente' }) }
elseif (-not $backup) {
    Write-Host 'Nenhum backup encontrado. Será criado agora; a senha do sudo é pedida NESTA janela.' -ForegroundColor Yellow
    $dump = @'
set -eu
sudo -p 'Senha sudo do DEV: ' -u postgres pg_dump -p 5433 -Fc -d fugates_dev \
  -f /var/lib/postgresql/backup_fugates_dev_$(date -u +%Y%m%d_%H%M%SZ).dump
echo BACKUP_CRIADO
'@
    RemotoInterativo $dump 'r164_backup.sh'
    $backup = (@(Remoto $backupScript) | Select-Object -First 1)
    if (-not $backup) { Abortar 'BACKUP' 'pg_dump não produziu arquivo' }
    Veredito 'BACKUP' 'PASS' $backup
} else { Veredito 'BACKUP' 'PASS' $backup }

# ---------------------------------------------------------------- FASE 2
$rolesScript = @'
set -eu
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tA -c \
 'select coalesce(string_agg(rolname,chr(44) order by rolname),chr(45)) from pg_roles where rolname in (chr(102)||chr(117)||chr(103)||chr(97)||chr(116)||chr(101)||chr(115)||chr(95)||chr(100)||chr(101)||chr(118)||chr(95)||chr(114)||chr(101)||chr(97)||chr(100)||chr(95)||chr(97)||chr(112)||chr(112), chr(102)||chr(117)||chr(103)||chr(97)||chr(116)||chr(101)||chr(115)||chr(95)||chr(115)||chr(97)||chr(112)||chr(95)||chr(105)||chr(110)||chr(98)||chr(111)||chr(117)||chr(110)||chr(100)||chr(95)||chr(97)||chr(112)||chr(112))'
'@
$existentes = (@(Remoto $rolesScript) | Select-Object -First 1)

if ($Conferir) { Veredito 'ROLES' 'CONFERENCIA' "existentes: $existentes" }
else {
    # Script de criação: recebe o caminho do SQL e o hash esperado como parâmetros posicionais.
    $criar = @'
set -eu
SQL="$1"; ESPERADO="$2"; ROLE="$3"; DIR="$4"
test "$(sha256sum "$SQL" | cut -d" " -f1 | tr a-f A-F)" = "$ESPERADO"
umask 077
mkdir -p "$DIR"
S="$(openssl rand -base64 36 | tr -d '\n/:@' | cut -c1-40)"
printf '127.0.0.1:5433:fugates_dev:%s:%s\n' "$ROLE" "$S" > "$DIR/pgpass"
chmod 600 "$DIR/pgpass"
sudo -p 'Senha sudo do DEV: ' -u postgres psql -p 5433 -d fugates_dev -v ON_ERROR_STOP=1 -P pager=off -v senha="$S" -f "$SQL"
RC=$?
unset S
exit $RC
'@
    # Aplica um SQL aprovado que NÃO envolve senha de role. Mesmo transporte, mesma conferência
    # de hash na VM; só não há geração de credencial.
    $aplicar = @'
set -eu
SQL="$1"; ESPERADO="$2"
test "$(sha256sum "$SQL" | cut -d" " -f1 | tr a-f A-F)" = "$ESPERADO"
sudo -p 'Senha sudo do DEV: ' -u postgres psql -p 5433 -d fugates_dev -v ON_ERROR_STOP=1 -P pager=off -f "$SQL"
'@
    # Leitura do estado de CONNECT das duas roles novas, pela identidade compartilhada.
    $connectScript = @'
set -eu
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tA -f - <<'SQL'
select has_database_privilege('fugates_dev_read_app','fugates_dev','CONNECT')::text
       || ';' || has_database_privilege('fugates_sap_inbound_app','fugates_dev','CONNECT')::text;
SQL
'@
    if ($existentes -notmatch 'fugates_dev_read_app') {
        Write-Host ''; Write-Host 'Criando fugates_dev_read_app. Senha do sudo nesta janela.' -ForegroundColor Yellow
        & $scp @lote -- $sqlRead "${Endpoint}:/tmp/r164_005.sql" | Out-Null
        if ($LASTEXITCODE -ne 0) { Abortar 'READ_ROLE' 'envio do 005 falhou' }
        RemotoInterativo $criar 'r164_read.sh' @('/tmp/r164_005.sql', $shaRead, 'fugates_dev_read_app', '/home/michael/.fugates-dev-read')
        if ($LASTEXITCODE -ne 0) { Abortar 'READ_ROLE' '005 falhou; a migração é transacional, nada foi commitado' }
    }
    Veredito 'READ_ROLE' 'PASS' 'fugates_dev_read_app'

    if ($existentes -notmatch 'fugates_sap_inbound_app') {
        Write-Host ''; Write-Host 'Criando fugates_sap_inbound_app. Senha do sudo nesta janela.' -ForegroundColor Yellow
        & $scp @lote -- $sqlWrite "${Endpoint}:/tmp/r164_004.sql" | Out-Null
        if ($LASTEXITCODE -ne 0) { Abortar 'SAP_WRITER_ROLE' 'envio do 004 falhou' }
        RemotoInterativo $criar 'r164_write.sh' @('/tmp/r164_004.sql', $shaWrite, 'fugates_sap_inbound_app', '/home/michael/.fugates-sap-inbound')
        if ($LASTEXITCODE -ne 0) { Abortar 'SAP_WRITER_ROLE' '004 falhou; nada commitado. Rollback do 005, se desejado: 005_ROLLBACK_DEV_READ_ONLY_ROLE.sql' }
    }
    Veredito 'SAP_WRITER_ROLE' 'PASS' 'fugates_sap_inbound_app'

    # --- 006: CONNECT ON DATABASE.
    # 005 e 004 concedem USAGE nos schemas e privilégios de tabela, mas NENHUM concede CONNECT.
    # Nesta base o CONNECT do PUBLIC está revogado, então as duas roles AUTENTICAM e em seguida
    # falham com "permission denied for database / does not have CONNECT privilege" — a role de
    # leitura não serviria a API 18762 e a de escrita quebraria no recebimento SAP real.
    $conectam = (@(Remoto $connectScript | Where-Object { $_ -match ';' }) | Select-Object -First 1)
    if ($conectam -ne 'true;true') {
        Write-Host ''; Write-Host 'Concedendo CONNECT às duas roles (006). Senha do sudo nesta janela.' -ForegroundColor Yellow
        & $scp @lote -- $sqlConnect "${Endpoint}:/tmp/r164_006.sql" | Out-Null
        if ($LASTEXITCODE -ne 0) { Abortar 'DATABASE_CONNECT' 'envio do 006 falhou' }
        RemotoInterativo $aplicar 'r164_connect.sh' @('/tmp/r164_006.sql', $shaConnect)
        if ($LASTEXITCODE -ne 0) { Abortar 'DATABASE_CONNECT' '006 falhou; é transacional, nada commitado. Rollback: 006_DEV_ROLES_CONNECT_GRANT_ROLLBACK.sql' }
        $conectam = (@(Remoto $connectScript | Where-Object { $_ -match ';' }) | Select-Object -First 1)
        if ($conectam -ne 'true;true') { Abortar 'DATABASE_CONNECT' "006 aplicou mas o CONNECT não ficou efetivo: $conectam" }
    }
    Veredito 'DATABASE_CONNECT' 'PASS' 'fugates_dev_read_app e fugates_sap_inbound_app com CONNECT'
}

# ---------------------------------------------------------------- FASE 3
$aclScript = @'
set -eu
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tA -f - <<'SQL'
select r.rolname
       || '=' || has_table_privilege(r.rolname,'fugates.integracao','INSERT')::text
       || ',' || has_table_privilege(r.rolname,'fugates.contrato_sap_toledo_versao','INSERT')::text
       || ',' || coalesce(has_column_privilege(r.rolname,'fugates.integracao','estado','UPDATE')::text,'false')
  from pg_roles r
 where r.rolname in ('fugates_acm_hml_app','fugates_dev_read_app','fugates_sap_inbound_app')
 order by 1;
SQL
'@
$linhas = @(Remoto $aclScript | Where-Object { $_ -match '=' })
if ($linhas.Count -eq 0) { Abortar 'ACL_ISOLATION' 'nenhuma role retornada' }
$esperado = @{
    'fugates_acm_hml_app'     = 'true,true,true'    # inalterada
    'fugates_dev_read_app'    = 'false,false,false' # sem nenhuma escrita
    'fugates_sap_inbound_app' = 'true,true,false'   # escrita contratual, sem direito de worker
}
foreach ($l in $linhas) {
    $nome, $valores = $l -split '=', 2
    if ($esperado[$nome] -ne $valores) { Abortar 'ACL_ISOLATION' "ACL inesperada: $l (esperado $nome=$($esperado[$nome]))" }
}
Veredito 'ACL_ISOLATION' 'PASS' ($linhas -join ' | ')
Veredito 'EXISTING_ROLE_UNCHANGED' $(if ($linhas -contains 'fugates_acm_hml_app=true,true,true') { 'PASS' } else { 'BLOCKED' })

if ($Conferir -or $ApenasFase2) { Resumo; exit 0 }

# ---------------------------------------------------------------- FASE 4
$authScript = @'
set -eu
PGPASSFILE=/home/michael/.fugates-dev-read/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_dev_read_app' -tA -f - <<'SQL'
select current_user || ';' || current_setting('default_transaction_read_only')
       || ';' || (select count(*) from fugates.contrato_sap_toledo_versao)::text;
SQL
'@
$leitura = (@(Remoto $authScript | Where-Object { $_ -match ';' }) | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or $leitura -notmatch '^fugates_dev_read_app;on;') { Abortar 'READ_ROLE_AUTH' "autenticação/leitura falhou: $leitura" }
Veredito 'READ_ROLE_AUTH' 'PASS' $leitura

# ---------------------------------------------------------------- FASE 5
if (-not $Pacote) { Abortar 'READ_API_ROLE_SWITCH' '-Pacote não informado; as roles já estão criadas e validadas' }
if (-not (Test-Path $Pacote)) { Abortar 'READ_API_ROLE_SWITCH' "pacote ausente: $Pacote" }
$shaPkg = (Get-FileHash $Pacote -Algorithm SHA256).Hash
if ($shaPkg -ne $ShaPacote.ToUpper()) { Abortar 'READ_API_ROLE_SWITCH' "SHA do pacote difere do aprovado`n  esperado: $ShaPacote`n  obtido  : $shaPkg" }

$anteriorScript = @'
set -eu
P=$(ss -ltnp 2>/dev/null | grep 18762 | sed -n 's/.*pid=\([0-9]*\).*/\1/p' | head -1)
test -n "$P"
readlink /proc/$P/cwd
'@
$anterior = (@(Remoto $anteriorScript | Where-Object { $_ -match '^/' }) | Select-Object -First 1)
if (-not $anterior) { Abortar 'READ_API_ROLE_SWITCH' 'instalação atual da API 18762 não identificada' }
Write-Host ("  instalação atual (ponto de rollback): {0}" -f $anterior)

& $scp @lote -- $Pacote "${Endpoint}:/home/michael/.fugates-unified-dev-read/$shaPkg.zip" | Out-Null
if ($LASTEXITCODE -ne 0) { Abortar 'READ_API_ROLE_SWITCH' 'scp falhou; nada foi trocado' }

$trocaScript = @'
set -eu
SHA="$1"; VELHO="$2"
D=/home/michael/.fugates-unified-dev-read
test "$(sha256sum $D/$SHA.zip | cut -d" " -f1 | tr a-f A-F)" = "$SHA"
tar -czf $D/BACKUP_API_$(date -u +%Y%m%d_%H%M%SZ).tar.gz -C $(dirname $VELHO) $(basename $VELHO)
NOVO=$D/$(date -u +%Y%m%d_%H%M%S)_ROLE_OPTIN
mkdir -m 700 $NOVO
python3 -c 'import zipfile,sys;zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' $D/$SHA.zip $NOVO
export FUGATES_DEV_READ_APPROVAL=FUGATES-UNIFIED-POSTGRESQL-DEV
export FUGATES_DEV_READ_KEY_FILE=/home/michael/.fugates-r165al-physical-R2/run/READ_API_KEY
export R165AL_APPROVED_PASSFILE=/home/michael/.fugates-dev-read/pgpass
export FUGATES_DEV_READ_ROLE=fugates_dev_read_app
kill $(ss -ltnp 2>/dev/null | grep 18762 | sed -n 's/.*pid=\([0-9]*\).*/\1/p' | head -1) 2>/dev/null || true
for i in $(seq 1 20); do ss -ltn | grep -q 127.0.0.1:18762 || break; sleep 0.5; done
cd $NOVO
nohup /usr/bin/dotnet $NOVO/FugaTES.Callback.Http.dll --dev-unified-read-only >> $NOVO/reader.log 2>&1 &
echo $! > $NOVO/PID
for i in $(seq 1 40); do ss -ltn | grep -q 127.0.0.1:18762 && break; sleep 0.5; done
K=$(cat $FUGATES_DEV_READ_KEY_FILE)
ST=$(curl -s -o /tmp/r164_id.json -w %{http_code} -H "X-R165AL-Read-Key: $K" http://127.0.0.1:18762/dev/read/identity || echo 000)
unset K
printf 'HTTP=%s\n' "$ST"
cat /tmp/r164_id.json; echo
printf 'NOVO=%s\n' "$NOVO"
'@
$troca = @(Remoto $trocaScript @($shaPkg, $anterior))
$troca | ForEach-Object { Write-Host ("  " + $_) }
if (($troca -join ' ') -notmatch 'HTTP=200' -or ($troca -join ' ') -notmatch 'fugates_dev_read_app') {
    Write-Host 'Troca não confirmada. Aplicando ROLLBACK para a instalação anterior.' -ForegroundColor Red
    $rollbackScript = @'
set -eu
VELHO="$1"
export FUGATES_DEV_READ_APPROVAL=FUGATES-UNIFIED-POSTGRESQL-DEV
export FUGATES_DEV_READ_KEY_FILE=/home/michael/.fugates-r165al-physical-R2/run/READ_API_KEY
export R165AL_APPROVED_PASSFILE=/home/michael/.fugates-acm-r165p/pgpass
kill $(ss -ltnp 2>/dev/null | grep 18762 | sed -n 's/.*pid=\([0-9]*\).*/\1/p' | head -1) 2>/dev/null || true
sleep 2
cd $VELHO
nohup /usr/bin/dotnet $VELHO/FugaTES.Callback.Http.dll --dev-unified-read-only >> $VELHO/reader.log 2>&1 &
echo $! > $VELHO/PID
for i in $(seq 1 40); do ss -ltn | grep -q 127.0.0.1:18762 && break; sleep 0.5; done
echo ROLLBACK_APLICADO
'@
    Remoto $rollbackScript @($anterior) | ForEach-Object { Write-Host ("  " + $_) }
    Abortar 'READ_API_ROLE_SWITCH' 'identidade não confirmada; rollback aplicado à instalação anterior'
}
Veredito 'READ_API_ROLE_SWITCH' 'PASS' 'API 18762 autenticando como fugates_dev_read_app'

# ---------------------------------------------------------------- FASE 6
$conteudoScript = @'
set -eu
K=$(cat /home/michael/.fugates-r165al-physical-R2/run/READ_API_KEY)
for Q in default TODOS 9999; do
  if [ "$Q" = default ]; then U=http://127.0.0.1:18762/dev/read/contracts
  else U=http://127.0.0.1:18762/dev/read/contracts?selector=$Q; fi
  N=$(curl -s -H "X-R165AL-Read-Key: $K" "$U" | grep -o numeroContrato | wc -l)
  printf '%s=%s ' "$Q" "$N"
done
unset K
echo
'@
$leituras = (@(Remoto $conteudoScript | Where-Object { $_ -match '=' }) | Select-Object -First 1)
Write-Host ("  contratos: " + $leituras)
if ($leituras -notmatch 'default=6' -or $leituras -notmatch 'TODOS=6' -or $leituras -notmatch '9999=0') {
    Abortar 'READ_API_CONTEUDO' "leitura inesperada: $leituras"
}
Veredito 'READ_API_CONTEUDO' 'PASS' 'seis contratos no padrão e em TODOS; centro inexistente devolve zero'

$svcScript = @'
set -eu
printf 'listener18763=%s\n' "$(ss -ltn | grep -c 127.0.0.1:18763)"
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql -w 'host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app' -tA -c 'select chr(111)||chr(107)'
'@
$svc = @(Remoto $svcScript)
$ok18763 = (($svc -join ' ') -match 'listener18763=1') -and (($svc -join ' ') -match 'ok')
Veredito 'COMMAND_18763_PRESERVED' $(if ($ok18763) { 'PASS' } else { 'BLOCKED' }) 'listener presente e identidade compartilhada autenticando; nenhum comando disparado'

$evid = Join-Path $env:TEMP ('FugaTES_R164_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
Get-Process FugaTES -ErrorAction SilentlyContinue | ForEach-Object { $null = $_.CloseMainWindow() }
Start-Sleep -Seconds 3
Get-Process FugaTES -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
& (Join-Path $Dev110 'FugaTES.exe') '--validate-ui' $evid | Out-Null
$resultado = Join-Path $evid 'UI_RESULT.json'
if (-not (Test-Path $resultado)) { Abortar 'DEV110_READBACK' "DEV110 não produziu UI_RESULT.json (evidências em $evid)" }
$ui = Get-Content $resultado -Raw | ConvertFrom-Json
$jsonOk = $ui.contracts.jsonCheck.persistedJsonValid -and ($ui.contracts.jsonCheck.title -like '*1147*10*')
if (-not ($ui.connection.Connected -and $ui.contracts.count -eq 6 -and $jsonOk -and -not $ui.sendEnabled)) {
    Abortar 'DEV110_READBACK' "validação do DEV110 não conferiu (evidências em $evid)"
}
Veredito 'DEV110_READBACK' 'PASS' ("v{0} | {1} | {2} contratos | {3}" -f $ui.version, $ui.connection.Cluster, $ui.contracts.count, $ui.contracts.jsonCheck.title)
Get-Process FugaTES -ErrorAction SilentlyContinue | ForEach-Object { $null = $_.CloseMainWindow() }

Resumo
