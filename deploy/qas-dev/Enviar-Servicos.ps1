# Envia para a VM os dois pacotes do QAS120 e os dois scripts que os instalam.
#
# POR QUE A CONFERENCIA CRUZADA DE SHA. Em 06/11/2026 um remetente levava o SHA esperado escrito
# dentro dele, e o pacote tinha sido reconstruido: o SHA do script nao batia com o do arquivo, e a
# publicacao instalou o PACOTE ANTIGO duas vezes, com mensagem de sucesso nas duas. O numero certo
# e o que esta no instalador - entao e dele que este script LE o esperado, em vez de ter uma copia
# propria para divergir.
#
# Uso:  .\Enviar-Servicos.ps1

[CmdletBinding()]
param([string]$Destino = 'michael@192.168.17.40')

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$instalador = 'instalar-servicos-qas.sh'
$pacotes = @{
    'publicacao/fugates-callback.tgz' = 'SHA_CB'
    'publicacao/fugates-motor.tgz'    = 'SHA_MT'
}

$texto = Get-Content -LiteralPath $instalador -Raw

foreach ($par in $pacotes.GetEnumerator()) {
    $arquivo = $par.Key
    $chave   = $par.Value

    if (-not (Test-Path -LiteralPath $arquivo)) { throw "Pacote ausente: $arquivo" }

    $m = [regex]::Match($texto, "(?m)^$chave='([0-9a-f]{64})'")
    if (-not $m.Success) { throw "Nao achei $chave no $instalador." }
    $esperado = $m.Groups[1].Value

    $real = (Get-FileHash -LiteralPath $arquivo -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($real -ne $esperado) {
        Write-Host ''
        Write-Host "O pacote $arquivo NAO e o que o instalador espera." -ForegroundColor Red
        Write-Host "  no $instalador ($chave): $esperado"
        Write-Host "  no arquivo ............. $real"
        Write-Host ''
        Write-Host 'Um dos dois esta velho. Reconstrua o pacote, ou corrija o SHA no instalador.'
        Write-Host 'Nada foi enviado.'
        throw "SHA divergente em $arquivo."
    }
    Write-Host ("ok  {0}  {1}..." -f $arquivo, $real.Substring(0,16))
}

Write-Host ''
Write-Host 'enviando pacotes e scripts'
scp publicacao/fugates-callback.tgz publicacao/fugates-motor.tgz `
    instalar-servicos-qas.sh instalar-rotas-qas.sh conferir-guarda-motor.sh "${Destino}:~/"
if ($LASTEXITCODE -ne 0) { throw "scp falhou com codigo $LASTEXITCODE." }

Write-Host ''
Write-Host 'Na VM, nesta ordem:'
Write-Host ''
Write-Host '  chmod +x ~/instalar-servicos-qas.sh ~/instalar-rotas-qas.sh'
Write-Host ''
Write-Host '  # 1) nada e alterado: so mostra o que existe e as CHAVES de ambiente do DEV'
Write-Host '  sudo ~/instalar-servicos-qas.sh conferir'
Write-Host ''
Write-Host '  # 2) extrai os releases do QAS e cria os dois EnvironmentFile com <PREENCHER>'
Write-Host '  sudo ~/instalar-servicos-qas.sh preparar'
Write-Host ''
Write-Host '  # 3) VOCE preenche. A cadeia de conexao e a senha sao suas; eu nao as escrevo.'
Write-Host '  sudo nano /etc/fugates/qas120/callback.env'
Write-Host '  sudo nano /etc/fugates/qas120/motor.env'
Write-Host ''
Write-Host '  # 4) sobe as duas unidades do QAS. Recusa se o par (banco, papel), o sap-client'
Write-Host '  #    ou a rota de retorno nao forem do QAS120.'
Write-Host '  sudo ~/instalar-servicos-qas.sh aplicar MARCA=<a marca que o preparar imprimiu>   # ou so a marca'
Write-Host ''
Write-Host '  # 5) as rotas. O conferir mostra de onde as origens serao copiadas.'
Write-Host '  sudo ~/instalar-rotas-qas.sh conferir'
Write-Host '  sudo ~/instalar-rotas-qas.sh aplicar'
Write-Host ''
Write-Host 'Para desfazer, em qualquer ponto:'
Write-Host '  sudo ~/instalar-rotas-qas.sh reverter      # devolve o nginx'
Write-Host '  sudo ~/instalar-servicos-qas.sh reverter   # remove as unidades do QAS'
Write-Host 'Nenhum dos dois toca no DEV110.'
