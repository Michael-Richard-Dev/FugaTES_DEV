<#
    R158 - Validacao OFFLINE do pacote do receptor SAP DEV.

    NAO instala, NAO ativa, NAO conecta em banco, NAO envia nada ao SAP ou ao Guardian.
    Apenas inspeciona o arquivo de configuracao e devolve READY ou BLOCKED com o motivo.

    Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File .\Validar-ReceptorDev.ps1
#>
[CmdletBinding()]
param(
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot nao esta disponivel durante a ligacao de parametros no Windows PowerShell 5.1.
if ([string]::IsNullOrWhiteSpace($ConfigPath)) { $ConfigPath = Join-Path $PSScriptRoot 'appsettings.receiver.dev.json' }
$problemas = New-Object System.Collections.Generic.List[string]

function Reprovar([string]$motivo) { $problemas.Add($motivo) }

if (-not (Test-Path $ConfigPath)) { Write-Output 'RECEIVER_PACKAGE=BLOCKED'; Write-Output "  configuracao ausente: $ConfigPath"; exit 1 }

$texto = Get-Content $ConfigPath -Raw
$cfg = $texto | ConvertFrom-Json

# 1) pendencias explicitas mantem o pacote fechado
$pendentes = [regex]::Matches($texto, '<<PENDENTE[^>]*>>') | ForEach-Object { $_.Value } | Sort-Object -Unique
if ($pendentes.Count -gt 0) { Reprovar ("pendencias nao preenchidas: " + ($pendentes -join ', ')) }

# 2) destino de banco: exclusivamente fugates_dev:5433 com a role ACM
$conexao = $cfg.ConnectionStrings.FugaTES
foreach ($exigido in @('Port=5433', 'Database=fugates_dev', 'Username=fugates_sap_inbound_app', 'Search Path=fugates')) {
    if ($conexao -notlike "*$exigido*") { Reprovar "conexao sem '$exigido'" }
}
foreach ($proibido in @('Port=5432', 'Port=5434', 'Port=15432', 'Port=15433', 'Port=15434')) {
    if ($conexao -like "*$proibido*") { Reprovar "conexao aponta para destino proibido '$proibido'" }
}
if ($conexao -like '*Password=*') { Reprovar 'conexao contem senha em texto claro; use Passfile' }
if ($conexao -notlike '*Passfile=/*') { Reprovar 'conexao sem Passfile absoluto' }

# 3) guards de seguranca e origem preservados
if ($cfg.SapContractInbound.SecurityMode -ne 'LoopbackBasicHttps') { Reprovar 'SecurityMode deve ser LoopbackBasicHttps' }
if (-not $cfg.SapContractInbound.ProductiveEnabled) { Reprovar 'ProductiveEnabled deve ser true no pacote do receptor' }
if ($cfg.SapContractInbound.OfflineEnabled) { Reprovar 'OfflineEnabled nao pode coexistir com o modo produtivo' }

# 4) nada de worker, Guardian, callback ou comando como efeito colateral
if ($cfg.FUGATES_INTEGRATION_WORKER_ENABLED) { Reprovar 'worker de integracao nao pode ser habilitado neste incremento' }
if ($cfg.Callback.Enabled) { Reprovar 'callback Guardian nao pode ser habilitado neste pacote' }
if ($cfg.AcmR57.Normal.ExecutionAuthorized) { Reprovar 'AcmR57.Normal.ExecutionAuthorized deve permanecer false' }
if ($cfg.AcmProtocolCommand.Enabled -or $cfg.AcmProtocolCommand.ExecutionAuthorized) { Reprovar 'AcmProtocolCommand deve permanecer desabilitado' }
if ($cfg.Sap.Enabled) { Reprovar 'Sap.Enabled deve permanecer false (nenhum POST SAP de saida)' }

# 5) instancia isolada: nao reusar as portas da leitura (18762) nem dos comandos (18763)
foreach ($reservada in @('18762', '18763')) {
    if ($cfg.Urls -like "*$reservada*") { Reprovar "a instancia do receptor nao pode usar a porta reservada $reservada" }
}

# 6) escopo ACM: janela R57 autorizada e time-boxed, ou piloto explicito
$janela = $cfg.MotorIntegracao.AcmInboundWindow
$piloto = $cfg.MotorIntegracao.AcmPilotNumeroContrato -or $cfg.MotorIntegracao.AcmPilotCentro -or $cfg.MotorIntegracao.AcmPilotItem
if ($janela.Enabled) {
    if ($janela.Centro -ne '3011') { Reprovar 'a janela exige Centro 3011' }
    if (-not ($janela.WindowId -match '^[0-9a-f]{32}$')) { Reprovar 'WindowId deve ser um GUID no formato N' }
    if ([string]::IsNullOrWhiteSpace($janela.AuthorizationReference) -or
        @('PENDENTE','NOT_APPROVED','NAO VALIDADO') -contains $janela.AuthorizationReference) { Reprovar 'AuthorizationReference da janela ausente ou nao aprovada' }
    if ($null -eq $janela.NotBeforeUtc -or $null -eq $janela.UntilUtc) { Reprovar 'janela sem NotBeforeUtc/UntilUtc' }
    else {
        $de = [datetimeoffset]::Parse($janela.NotBeforeUtc); $ate = [datetimeoffset]::Parse($janela.UntilUtc)
        if ($ate -le $de) { Reprovar 'janela com intervalo invalido' }
        elseif (($ate - $de) -gt [timespan]::FromDays(1)) { Reprovar 'janela excede 1 dia' }
    }
    if (-not $janela.AllowedSapPartners -or $janela.AllowedSapPartners.Count -eq 0) { Reprovar 'AllowedSapPartners nao pode ficar vazio' }
    if (-not $janela.AllowedSapMaterials -or $janela.AllowedSapMaterials.Count -eq 0) { Reprovar 'AllowedSapMaterials nao pode ficar vazio' }
} elseif (-not $piloto) {
    Reprovar 'escopo ACM ausente: sem janela habilitada e sem piloto, a composicao recusa com SAP_INBOUND_AUTOMATIC_SCOPED_R57_BINDING_REQUIRED'
}

Write-Output ("CONFIG={0}" -f $ConfigPath)
Write-Output ("TARGET_DATABASE=fugates_dev:5433")
Write-Output ("WINDOW_ENABLED={0}" -f [bool]$janela.Enabled)
Write-Output ("WORKERS_ENABLED={0}" -f [bool]$cfg.FUGATES_INTEGRATION_WORKER_ENABLED)
if ($problemas.Count -eq 0) {
    Write-Output 'RECEIVER_PACKAGE=READY_FOR_HERA_AUTHORIZED_INSTALL'
    Write-Output '  Nenhuma instalacao foi executada por este script.'
    exit 0
}
Write-Output 'RECEIVER_PACKAGE=BLOCKED'
$problemas | ForEach-Object { Write-Output ("  - " + $_) }
exit 1
