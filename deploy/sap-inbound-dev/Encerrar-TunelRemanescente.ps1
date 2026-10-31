<#
    R160 - Encerramento seguro de tunel SSH remanescente do FugaTES (127.0.0.1:18761).

    Quando o FugaTES e encerrado de forma normal, LocalReadTunnel.Dispose() mata apenas o processo
    ssh que ele mesmo iniciou e preserva tuneis de terceiros. Se o aplicativo for finalizado a
    forca (Stop-Process, travamento, fim de sessao), o Dispose nao roda e o ssh filho sobrevive,
    deixando um listener remanescente em 127.0.0.1:18761.

    Este script encerra esse remanescente e SOMENTE ele. Um processo ssh e considerado do FugaTES
    apenas se satisfizer, ao mesmo tempo:
      1. ser o unico listener de 127.0.0.1:18761;
      2. ter ExecutablePath igual ao OpenSSH do Windows (%SystemRoot%\System32\OpenSSH\ssh.exe);
      3. ter na linha de comando o encaminhamento exato -L 127.0.0.1:18761:127.0.0.1:<porta remota>
         declarada no perfil, e o IdentityFile do perfil.
    Qualquer divergencia => RECUSADO, sem encerrar nada.

    NAO encerra processos ssh de terceiros. NAO toca a VM. NAO altera configuracao SSH.

    Uso:
      powershell -NoProfile -ExecutionPolicy Bypass -File .\Encerrar-TunelRemanescente.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File .\Encerrar-TunelRemanescente.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ProfilePath,
    [int]$LocalPort = 18761
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProfilePath)) { $ProfilePath = 'C:\FugaTES_DEV110\FugaTES.unified.json' }

if (-not (Test-Path $ProfilePath)) { Write-Output 'SSH_TUNNEL_CLEANUP=BLOCKED'; Write-Output "  perfil ausente: $ProfilePath"; exit 1 }
$perfil = Get-Content $ProfilePath -Raw | ConvertFrom-Json
$tunel = $perfil.Tunnel
if ($null -eq $tunel -or -not $tunel.Enabled) { Write-Output 'SSH_TUNNEL_CLEANUP=NOT_REQUIRED'; Write-Output '  perfil nao declara tunel habilitado.'; exit 0 }

$sshEsperado = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$encaminhamento = "-L 127.0.0.1:{0}:127.0.0.1:{1}" -f $LocalPort, $tunel.RemoteReadPort

$conexoes = @(Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue)
if ($conexoes.Count -eq 0) { Write-Output 'SSH_TUNNEL_CLEANUP=NOT_REQUIRED'; Write-Output "  nenhum listener em 127.0.0.1:$LocalPort."; exit 0 }
if ($conexoes.Count -ne 1 -or $conexoes[0].LocalAddress -ne '127.0.0.1') {
    Write-Output 'SSH_TUNNEL_CLEANUP=BLOCKED'
    Write-Output "  listener inesperado em $LocalPort ($($conexoes.Count) conexoes); nada foi encerrado."
    exit 1
}

$pid_ = $conexoes[0].OwningProcess
$proc = Get-CimInstance Win32_Process -Filter ("ProcessId=$pid_") -ErrorAction SilentlyContinue
if ($null -eq $proc) { Write-Output 'SSH_TUNNEL_CLEANUP=BLOCKED'; Write-Output "  processo $pid_ nao identificavel; nada foi encerrado."; exit 1 }

$motivos = New-Object System.Collections.Generic.List[string]
if ($proc.ExecutablePath -ne $sshEsperado) { $motivos.Add("executavel '$($proc.ExecutablePath)' nao e o OpenSSH do Windows") }
if ($proc.CommandLine -notlike "*$encaminhamento*") { $motivos.Add("linha de comando sem o encaminhamento '$encaminhamento'") }
if ($proc.CommandLine -notlike "*$($tunel.IdentityFile)*") { $motivos.Add('linha de comando sem o IdentityFile do perfil') }

Write-Output ("PID={0}" -f $pid_)
Write-Output ("FORWARD_ESPERADO={0}" -f $encaminhamento)

if ($motivos.Count -gt 0) {
    Write-Output 'SSH_TUNNEL_CLEANUP=REFUSED_NOT_FUGATES'
    $motivos | ForEach-Object { Write-Output ("  - " + $_) }
    Write-Output '  Nenhum processo foi encerrado.'
    exit 1
}

$donoFugaTES = @(Get-Process FugaTES -ErrorAction SilentlyContinue)
if ($donoFugaTES.Count -gt 0) {
    Write-Output 'SSH_TUNNEL_CLEANUP=SKIPPED_APP_RUNNING'
    Write-Output "  FugaTES esta em execucao ($($donoFugaTES.Count) processo(s)); o tunel pertence a sessao ativa."
    exit 0
}

if ($PSCmdlet.ShouldProcess("ssh.exe PID $pid_", 'Encerrar tunel remanescente do FugaTES')) {
    Stop-Process -Id $pid_ -Force
    Start-Sleep -Milliseconds 800
    $restante = @(Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue)
    if ($restante.Count -eq 0) { Write-Output 'SSH_TUNNEL_CLEANUP=PASS'; exit 0 }
    Write-Output 'SSH_TUNNEL_CLEANUP=BLOCKED'; Write-Output '  listener persistiu apos o encerramento.'; exit 1
}
Write-Output 'SSH_TUNNEL_CLEANUP=WHATIF'
