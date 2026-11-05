# Envia para a VM o script que libera a criacao do protocolo apos a criacao do contrato no ACM.
#
# NAO vai pacote nenhum: nao ha mudanca de codigo. O fluxo contrato -> protocolo ja esta construido;
# o que o faltava era a aprovacao de callback, que e configuracao.
#
# O Set-Location existe porque o scp le "C:" como host e falha; rodando a partir da pasta, os
# nomes vao relativos.
#
# Uso:  .\Enviar-ProtocoloAcm.ps1

[CmdletBinding()]
param(
    [string]$Destino = 'michael@192.168.17.40'
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

Write-Host "enviando para $Destino (a senha e pedida pelo proprio scp)"
scp habilitar-protocolo-acm.sh "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE. Nada foi alterado." }

Write-Host ''
Write-Host 'Enviado. Na VM:'
Write-Host ''
Write-Host '  1) conferir. NAO altera nada - ele reconfere os dois atestados:'
Write-Host '     sudo bash ~/habilitar-protocolo-acm.sh conferir'
Write-Host ''
Write-Host '  2) liberar, nomeando a conferencia feita:'
Write-Host '     sudo bash ~/habilitar-protocolo-acm.sh instalar --evidencia R173-CALLBACK-CONFERIDO-20261105'
Write-Host ''
Write-Host '     --evidencia nao tem padrao de proposito: a referencia tem de nomear a conferencia'
Write-Host '     que foi feita. Troque por outra se preferir outro texto.'
Write-Host ''
Write-Host '  Para desfazer:'
Write-Host '     sudo bash ~/habilitar-protocolo-acm.sh reverter'
