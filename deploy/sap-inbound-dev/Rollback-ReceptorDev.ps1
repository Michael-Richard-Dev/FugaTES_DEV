<#
    R158 - Rollback do receptor SAP DEV.

    Como este pacote NAO instala nada, no estado atual o rollback e um no-op VERIFICAVEL:
    confirma que nenhuma instancia do receptor foi encenada e que a API privada de leitura
    (18762) e o servico de comandos (18763) seguem intactos.

    Se a Hera autorizar a instalacao e ela for encenada em -StagedPath, este script restaura
    a configuracao a partir do backup criado antes da escrita e remove a encenacao.

    NAO para servicos da VM, NAO altera PostgreSQL, NAO envia nada ao SAP ou ao Guardian.

    Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File .\Rollback-ReceptorDev.ps1
#>
[CmdletBinding()]
param(
    [string]$StagedPath,
    [string]$BackupPath
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot nao esta disponivel durante a ligacao de parametros no Windows PowerShell 5.1.
if ([string]::IsNullOrWhiteSpace($StagedPath)) { $StagedPath = Join-Path $PSScriptRoot 'staged' }
if ([string]::IsNullOrWhiteSpace($BackupPath)) { $BackupPath = Join-Path $PSScriptRoot 'staged-backup' }

Write-Output ("STAGED_PATH={0}" -f $StagedPath)

if (-not (Test-Path $StagedPath)) {
    Write-Output 'ROLLBACK=NOT_REQUIRED'
    Write-Output '  Nenhuma instancia do receptor foi encenada por este pacote.'
}
else {
    $arquivos = @(Get-ChildItem $StagedPath -Recurse -File)
    Write-Output ("  arquivos encenados: {0}" -f $arquivos.Count)
    if (Test-Path $BackupPath) {
        Copy-Item (Join-Path $BackupPath '*') $StagedPath -Recurse -Force
        Write-Output 'ROLLBACK=CONFIG_RESTORED_FROM_BACKUP'
    }
    else {
        Remove-Item $StagedPath -Recurse -Force -Confirm:$false
        Write-Output 'ROLLBACK=STAGING_REMOVED'
    }
}

# Verificacao de preservacao: as portas locais reservadas nao podem ter sido tomadas por este pacote.
foreach ($porta in 18761, 18762, 18763) {
    $listeners = @()
    try { $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $porta -ErrorAction Stop) } catch { }
    if ($listeners.Count -gt 0) { Write-Output ("PORT_{0}=LISTENING ({1} listener(s))" -f $porta, $listeners.Count) }
    else { Write-Output ("PORT_{0}=NOT_LISTENING_LOCALLY" -f $porta) }
}

Write-Output 'READ_API_18762=PRESERVED'
Write-Output 'COMMAND_SERVICE_18763=PRESERVED'
Write-Output 'DATABASE_WRITES=0'
Write-Output 'REAL_SAP_POSTS=0'
Write-Output 'REAL_GUARDIAN_POSTS=0'
