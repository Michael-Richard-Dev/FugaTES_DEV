# Envia para a VM o release do motor ACM com o corte do cadastro automatico, e o publicador.
#
# O Set-Location existe porque o scp le "C:" como host e falha; rodando a partir da pasta, os
# nomes vao relativos.
#
# Uso:  .\Enviar-CorteAcm.ps1

[CmdletBinding()]
param(
    [string]$Destino = 'michael@192.168.17.40'
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

if (-not (Test-Path -LiteralPath 'fugates-motor-acm.tgz')) {
    throw 'Pacote ausente na pasta da publicacao: fugates-motor-acm.tgz'
}

$sha = (Get-FileHash -LiteralPath 'fugates-motor-acm.tgz' -Algorithm SHA256).Hash.ToLowerInvariant()
$esperado = '8142e683bd892e2ea514127576d59cf9d81531af7729538de00e1b184ee09559'
Write-Host "pacote ... fugates-motor-acm.tgz"
Write-Host "sha256 ... $sha"
Write-Host "esperado . $esperado"
if ($sha -ne $esperado) { throw 'O pacote nao confere com o SHA publicado. Nada foi enviado.' }

Write-Host ''
Write-Host "enviando para $Destino (a senha e pedida pelo proprio scp)"
scp fugates-motor-acm.tgz publicar-corte-acm.sh "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE. Nada foi instalado." }

Write-Host ''
Write-Host 'Enviado. Na VM, nesta ordem:'
Write-Host ''
Write-Host '  1) desempacotar:'
Write-Host '     rm -rf ~/r173 && mkdir ~/r173 && tar -xzf ~/fugates-motor-acm.tgz -C ~/r173'
Write-Host ''
Write-Host '  2) conferir. NAO altera nada. Mostra o recebimento mais recente no banco, o corte'
Write-Host '     que sera declarado, e quantas versoes de contrato sairao do recorte:'
Write-Host '     sudo bash ~/publicar-corte-acm.sh conferir'
Write-Host ''
Write-Host '  3) publicar o release E ligar o corte, num passo:'
Write-Host '     sudo bash ~/publicar-corte-acm.sh instalar'
Write-Host ''
Write-Host '  Para desfazer (a fila antiga volta ao recorte):'
Write-Host '     sudo bash ~/publicar-corte-acm.sh reverter'
