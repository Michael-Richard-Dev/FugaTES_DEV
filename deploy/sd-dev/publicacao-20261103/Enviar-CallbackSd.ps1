# Envia para a VM o pacote e o publicador do receptor compartilhado (Toledo/ACM + devolutiva SD).
#
# Nao instala nada: so copia. Quem publica e o publicar-callback-sd.sh, na VM, em tres passos.
#
# O Set-Location existe por um motivo concreto: o scp le "C:" como se fosse um host e falha com
# "ssh: Could not resolve hostname c". Rodando a partir da pasta, os nomes vao relativos.
#
# Uso:  .\Enviar-CallbackSd.ps1
#       .\Enviar-CallbackSd.ps1 -Destino michael@192.168.17.40

[CmdletBinding()]
param(
    [string]$Destino = 'michael@192.168.17.40'
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$arquivos = @('fugates-sd-devolutiva.tgz', 'publicar-callback-sd.sh')
foreach ($arquivo in $arquivos) {
    if (-not (Test-Path -LiteralPath $arquivo)) {
        throw "Arquivo ausente na pasta da publicacao: $arquivo"
    }
}

# O mesmo SHA que o publicador confere na VM antes de extrair qualquer coisa. Conferir dos dois
# lados e o que transforma "o arquivo chegou" em "o arquivo chegou inteiro".
$sha = (Get-FileHash -LiteralPath 'fugates-sd-devolutiva.tgz' -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "pacote ... fugates-sd-devolutiva.tgz"
Write-Host "sha256 ... $sha"
Write-Host "esperado . 94c29efb5cc7b14334b915f8fee6938ec4cfe5cf466d1fc938b0a78aaf88d80f"
if ($sha -ne '94c29efb5cc7b14334b915f8fee6938ec4cfe5cf466d1fc938b0a78aaf88d80f') {
    throw 'O pacote nao confere com o SHA publicado. Nada foi enviado.'
}

Write-Host ''
Write-Host "enviando para $Destino (a senha e pedida pelo proprio scp)"
scp fugates-sd-devolutiva.tgz publicar-callback-sd.sh "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE. Nada foi instalado." }

Write-Host ''
Write-Host 'Enviado. Na VM, nesta ordem (os dois primeiros nao alteram nada):'
Write-Host '  chmod +x ~/publicar-callback-sd.sh && sudo ~/publicar-callback-sd.sh conferir'
Write-Host '  sudo ~/publicar-callback-sd.sh ensaio'
Write-Host '  sudo ~/publicar-callback-sd.sh instalar'
