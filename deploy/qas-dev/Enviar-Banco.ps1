# Envia para a VM os scripts do QAS120. So copia: nada e criado daqui.
#
#   inventariar-cluster.sh   - so LE catalogo do PostgreSQL, nunca altera
#   inventariar-servicos.sh  - so LE systemd, nginx e portas, nunca altera
#   criar-banco-qas.sh       - "conferir" e "ensaio" nao alteram nada; "aplicar" cria
#
# Uso:  .\Enviar-Banco.ps1

[CmdletBinding()]
param([string]$Destino = 'michael@192.168.17.40')

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

Write-Host 'enviando os scripts do QAS120'
scp inventariar-cluster.sh criar-banco-qas.sh inventariar-servicos.sh "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE." }

Write-Host ''
Write-Host 'Na VM:'
Write-Host '  chmod +x ~/inventariar-cluster.sh ~/criar-banco-qas.sh'
Write-Host '  sudo ~/criar-banco-qas.sh conferir   # nao altera nada'
Write-Host '  sudo ~/criar-banco-qas.sh ensaio     # nao altera nada'
Write-Host '  sudo ~/criar-banco-qas.sh aplicar'
Write-Host ''
Write-Host '  chmod +x ~/inventariar-servicos.sh && sudo ~/inventariar-servicos.sh   # nao altera nada'
