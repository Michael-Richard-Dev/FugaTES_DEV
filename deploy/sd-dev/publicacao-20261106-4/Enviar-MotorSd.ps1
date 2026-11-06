# Envia para a VM o pacote do motor SD (fugates-sd-protocolo).
#
# Nao instala: quem instala e o instalar-motor-sd.sh, que ja esta na VM.
#
# O Set-Location existe porque o scp le "C:" como host e falha; rodando a partir da pasta, os
# nomes vao relativos.
#
# Uso:  .\Enviar-MotorSd.ps1

[CmdletBinding()]
param(
    [string]$Destino = 'michael@192.168.17.40'
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not (Test-Path -LiteralPath 'fugates-motor-sd.tgz')) {
    throw 'Pacote ausente na pasta da publicacao: fugates-motor-sd.tgz'
}

$sha = (Get-FileHash -LiteralPath 'fugates-motor-sd.tgz' -Algorithm SHA256).Hash.ToLowerInvariant()
$esperado = 'dc1394b3e112d1032a2878be42f0ba470c977d89d81c203b8d94e2d410916000'
Write-Host "pacote ... fugates-motor-sd.tgz"
Write-Host "sha256 ... $sha"
Write-Host "esperado . $esperado"
if ($sha -ne $esperado) { throw 'O pacote nao confere com o SHA publicado. Nada foi enviado.' }

Write-Host ''
Write-Host "enviando para $Destino (a senha e pedida pelo proprio scp)"
scp fugates-motor-sd.tgz "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE. Nada foi instalado." }

Write-Host ''
Write-Host 'Enviado. Na VM:'
Write-Host '  sudo ~/instalar-motor-sd.sh conferir'
Write-Host '  sudo ~/instalar-motor-sd.sh instalar'
