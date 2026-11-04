# Envia para a VM o pacote do motor ACM (fugates-acm-motor-dev) e o script que abre os dois centros.
#
# Nao instala nada: quem instala o release e o atualizar-motor-r170.sh, que ja esta na VM; quem abre
# os centros e o habilitar-centros-acm.sh, que vai neste envio.
#
# O Set-Location existe porque o scp le "C:" como host e falha; rodando a partir da pasta, os
# nomes vao relativos.
#
# Uso:  .\Enviar-MotorAcm.ps1

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
$esperado = 'ce478d522272ce53d10b7735794815caf82e077a2728764d63eb6fc88e2c507b'
Write-Host "pacote ... fugates-motor-acm.tgz"
Write-Host "sha256 ... $sha"
Write-Host "esperado . $esperado"
if ($sha -ne $esperado) { throw 'O pacote nao confere com o SHA publicado. Nada foi enviado.' }

Write-Host ''
Write-Host "enviando para $Destino (a senha e pedida pelo proprio scp)"
scp fugates-motor-acm.tgz publicar-motor-acm.sh "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE. Nada foi instalado." }

Write-Host ''
Write-Host 'Enviado. Na VM, nesta ordem:'
Write-Host ''
Write-Host '  1) desempacotar o release:'
Write-Host '     rm -rf ~/r172 && mkdir ~/r172 && tar -xzf ~/fugates-motor-acm.tgz -C ~/r172'
Write-Host ''
Write-Host '  2) conferir. NAO altera nada - leia a saida antes de seguir:'
Write-Host '     sudo bash ~/publicar-motor-acm.sh conferir'
Write-Host ''
Write-Host '  3) publicar o release E abrir os dois centros, num passo:'
Write-Host '     sudo bash ~/publicar-motor-acm.sh instalar'
Write-Host ''
Write-Host '     Um script so porque a unit tem drop-ins: o ultimo deles decide qual release roda,'
Write-Host '     e o arquivo principal da unit nao tem voz. O publicador troca o caminho DENTRO do'
Write-Host '     drop-in de maior precedencia e confere o ExecStart efetivo, nao o arquivo editado.'
Write-Host ''
Write-Host '  Para desfazer:'
Write-Host '     sudo bash ~/publicar-motor-acm.sh reverter'
Write-Host ''
Write-Host '  NAO use mais o atualizar-motor-r170.sh para este release: ele edita o arquivo'
Write-Host '  principal da unit, que os drop-ins sobrepoem, e anuncia sucesso sem efeito.'
