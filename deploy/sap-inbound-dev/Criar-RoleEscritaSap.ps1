<#
    R161 - Criacao interativa da role de escrita do receptor SAP no DEV 5433.

    POR QUE INTERATIVO: o DDL exige o superusuario postgres, alcancavel apenas por
    `sudo -u postgres` na VM, e o sudo pede senha. A senha e digitada SOMENTE neste terminal,
    pelo operador. Este script nao captura, nao registra e nao transporta senha alguma.

    O QUE FAZ:
      1. verifica o destino (cluster/porta/base/recovery) antes de qualquer escrita;
      2. exige backup fisico previo e confirma que ele existe;
      3. GERA na VM uma senha aleatoria para a role, grava no pgpass com modo 600 e NUNCA a exibe;
      4. aplica 004_SAP_INBOUND_WRITER_ROLE_DEV.sql, que tem guarda de destino e postcheck proprio;
      5. imprime um recibo sanitizado (sem senha, sem hash de senha).

    NAO abre janela R57, NAO instala receptor, NAO envia POST, NAO altera 5432/5434/HML/producao.

    Uso (no terminal do operador):
      powershell -NoProfile -ExecutionPolicy Bypass -File .\Criar-RoleEscritaSap.ps1 -Backup <caminho/do/backup>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Backup,
    [string]$Endpoint = 'michael@192.168.17.40',
    [string]$IdentityFile = 'C:\ProgramData\FugaTES\agentic\ssh\fugates-agentic-nt-ti-256',
    [string]$KnownHosts = 'C:\Users\TI\.ssh\known_hosts',
    [string]$Migracao,
    [string]$PassfileRemoto = '/home/michael/.fugates-sap-inbound/pgpass'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Migracao)) {
    $Migracao = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'docs\Incrementos\UnifiedDev\004_SAP_INBOUND_WRITER_ROLE_DEV.sql'
}
if (-not (Test-Path $Migracao)) { Write-Output 'SAP_WRITER_ROLE=BLOCKED'; Write-Output "  migracao ausente: $Migracao"; exit 1 }

$ssh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$opts = @('-tt', '-F', 'NUL', '-o', 'IdentitiesOnly=yes', '-o', 'IdentityAgent=none',
          '-o', 'StrictHostKeyChecking=yes', '-o', "UserKnownHostsFile=$KnownHosts",
          '-o', 'GlobalKnownHostsFile=NUL', '-o', 'ConnectTimeout=15', '-i', $IdentityFile)

$sqlBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Migracao))
$sqlSha = (Get-FileHash $Migracao -Algorithm SHA256).Hash

Write-Output ("MIGRACAO={0}" -f (Split-Path $Migracao -Leaf))
Write-Output ("MIGRACAO_SHA256={0}" -f $sqlSha)
Write-Output ("BACKUP_INFORMADO={0}" -f $Backup)
Write-Output 'A senha do sudo sera pedida a seguir e fica apenas neste terminal.'
Write-Output ''

# Script remoto: tudo numa unica sessao do operador. A senha da role e gerada na VM e nunca sai de la.
$remoto = @"
set -eu
test "`$(id -un)" = michael
test "`$(hostname)" = vm-fugatesbio
test -s '$Backup' || { echo 'SAP_WRITER_ROLE=BLOCKED'; echo '  backup informado nao existe ou esta vazio'; exit 1; }
echo "backup_bytes=`$(stat -c %s '$Backup')"
echo "backup_sha=`$(sha256sum '$Backup' | cut -d' ' -f1)"

umask 077
mkdir -p "`$(dirname '$PassfileRemoto')"
SENHA="`$(openssl rand -base64 36 | tr -d '\n/:@' | cut -c1-40)"
printf '127.0.0.1:5433:fugates_dev:fugates_sap_inbound_app:%s\n' "`$SENHA" > '$PassfileRemoto'
chmod 600 '$PassfileRemoto'

printf '%s' '$sqlBase64' | base64 -d > /tmp/004_sap_writer.sql
test "`$(sha256sum /tmp/004_sap_writer.sql | cut -d' ' -f1 | tr 'a-f' 'A-F')" = '$sqlSha' || { echo 'SAP_WRITER_ROLE=BLOCKED'; echo '  hash da migracao divergente apos transporte'; rm -f /tmp/004_sap_writer.sql; exit 1; }

sudo -p 'Senha sudo do DEV (somente neste terminal): ' -u postgres \
  psql -p 5433 -d fugates_dev -v ON_ERROR_STOP=1 -P pager=off \
       -v senha="`$SENHA" -f /tmp/004_sap_writer.sql
RC=`$?
unset SENHA
rm -f /tmp/004_sap_writer.sql
test `$RC -eq 0 || { echo 'SAP_WRITER_ROLE=BLOCKED'; exit `$RC; }

echo '--- postcheck sanitizado (sem senha) ---'
PGPASSFILE='$PassfileRemoto' psql "host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_sap_inbound_app" -tAc \
  "select 'autenticacao=OK role='||current_user||' db='||current_database()||' porta='||current_setting('port')"
PGPASSFILE=/home/michael/.fugates-acm-r165p/pgpass psql "host=127.0.0.1 port=5433 dbname=fugates_dev user=fugates_acm_hml_app" -tAc \
  "select v.tabela||':'||v.acao||'='||has_table_privilege('fugates_sap_inbound_app',v.tabela,v.acao)::text
     from (values ('fugates.contrato_sap_toledo_identidade','INSERT'),
                  ('fugates.contrato_sap_toledo_versao','INSERT'),
                  ('fugates.integracao','INSERT'),
                  ('fugates.integracao','UPDATE'),
                  ('fugates.contrato_sap_toledo_versao','DELETE')) v(tabela,acao)"
echo 'SAP_WRITER_ROLE=PASS'
echo "PASSFILE=$PassfileRemoto (modo 600, fora do codigo, dos logs e dos pacotes)"
"@

& $ssh @opts $Endpoint $remoto
exit $LASTEXITCODE
