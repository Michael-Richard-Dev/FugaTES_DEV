# Envia para a VM os dois scripts do banco do QAS120. So copia: nada e criado daqui.
#
#   inventariar-cluster.sh  - so LE catalogo, nunca altera
#   criar-banco-qas.sh      - "conferir" e "ensaio" nao alteram nada; "aplicar" cria
#
# Uso:  .\Enviar-Banco.ps1

[CmdletBinding()]
param([string]$Destino = 'michael@192.168.17.40')

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

Write-Host 'enviando inventariar-cluster.sh e criar-banco-qas.sh'
scp inventariar-cluster.sh criar-banco-qas.sh "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE." }

Write-Host ''
Write-Host 'Na VM:'
Write-Host '  chmod +x ~/inventariar-cluster.sh ~/criar-banco-qas.sh'
Write-Host '  sudo ~/criar-banco-qas.sh conferir   # nao altera nada'
Write-Host '  sudo ~/criar-banco-qas.sh ensaio     # nao altera nada'
Write-Host '  sudo ~/criar-banco-qas.sh aplicar'
