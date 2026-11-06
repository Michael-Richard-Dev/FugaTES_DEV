# Envia o inventario do cluster para a VM. Nao instala nada, nao altera nada: so copia um script
# que, por sua vez, so LE catalogo.
#
# Uso:  .\Enviar-Inventario.ps1

[CmdletBinding()]
param([string]$Destino = 'michael@192.168.17.40')

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

Write-Host 'enviando inventariar-cluster.sh (somente leitura de catalogo)'
scp inventariar-cluster.sh "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE." }

Write-Host ''
Write-Host 'Na VM:'
Write-Host '  chmod +x ~/inventariar-cluster.sh && sudo ~/inventariar-cluster.sh'
