# Publica o desktop (FugaTES.exe) em C:\FugaTES_DEV110.
#
# POR QUE ESTE SCRIPT EXISTE, e nao um "copia os arquivos":
#
#   - a pasta instalada guarda CONFIGURACAO ao lado dos binarios - FugaTES.carrier.json,
#     FugaTES.sd.json, os do MM, as pastas profiles e runtimes. Sobrescrever a pasta inteira
#     apagaria o perfil do ambiente, que e o que diz a qual SAP este desktop fala. Aqui so
#     "*.dll", "*.exe" e "*.pdb" sao trocados; nenhum .json e tocado;
#
#   - o aplicativo segura os DLLs enquanto roda. Copiar por cima com ele aberto falha no meio e
#     deixa a pasta com metade dos arquivos novos - pior do que nao ter comecado;
#
#   - e, sobretudo, porque relatorio de sucesso nao e prova. Em 06/11/2026 um publicador da VM
#     disse "no ar" duas vezes seguidas tendo instalado o pacote ANTIGO. Aqui, no fim, os
#     marcadores sao procurados DENTRO dos DLLs que ficaram na pasta.
#
# Uso:  .\Publicar-Desktop.ps1                 publica
#       .\Publicar-Desktop.ps1 -Conferir       nao altera nada: so diz o que esta instalado
#       .\Publicar-Desktop.ps1 -Destino D:\... outra pasta

[CmdletBinding()]
param(
    [string]$Destino = 'C:\FugaTES_DEV110',
    [switch]$Conferir
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$raiz = (Resolve-Path '..\..').Path

# Os marcadores conferidos no fim. Sao NOMES DE CODIGO das correcoes de 06/11/2026: se um deles
# some do binario instalado, aquela correcao nao esta la, qualquer que tenha sido a mensagem.
$marcadores = @(
    @{ Arquivo = 'FugaTES.Aplicacao.dll';      Marca = 'LacresPorTipo';             Porque = 'lacre de carga e de testemunha separados' },
    @{ Arquivo = 'FugaTES.Aplicacao.dll';      Marca = 'JsonDoGuardian';            Porque = 'Enter cru na observacao aceito' },
    @{ Arquivo = 'FugaTES.Aplicacao.dll';      Marca = 'TipoAusente';               Porque = 'pesagem sem pre-cadastro nao e recusa' },
    @{ Arquivo = 'FugaTES.Integracao.SAP.dll'; Marca = 'SapODataLoteV2';            Porque = 'itens da remessa numa transacao so' },
    @{ Arquivo = 'FugaTES.Integracao.SAP.dll'; Marca = 'currently being processed'; Porque = 'a trava da remessa e reconhecida' },
    # A rota do lote, e NAO o nome da variavel que a guarda usa: nome de variavel LOCAL nao fica
    # no assembly, fica no PDB. Conferido empiricamente contra a publicacao - "loteRemessa" dava
    # ausente num binario que a tinha. Marcador que nao se encontra nunca reprova tudo, inclusive
    # o que esta certo.
    @{ Arquivo = 'FugaTES.dll';                Marca = '/sap/opu/odata/sap/API_OUTBOUND_DELIVERY_SRV;v=2/$batch'; Porque = 'a guarda de rota conhece o lote' }
)

# Procura nas DUAS codificacoes: nome de tipo e de membro ficam em UTF-8 no assembly, literal de
# string fica em UTF-16. Procurar so numa delas acha metade e mente sobre a outra.
#
# A BUSCA E POR BYTES, e nao por texto decodificado. Decodificar o arquivo inteiro como UTF-16 so
# acha o literal se ele comecar em offset PAR - e, no primeiro teste deste script, "currently
# being processed" dava ausente num binario que o tinha, so por alinhamento. Converter os bytes
# com ISO-8859-1 preserva cada byte como um caractere e torna a posicao irrelevante.
#
# E o segundo verificador furado do dia: antes, "strings" nao existia na maquina e devolvia vazio
# em silencio. Quem confere tambem precisa ser conferido.
$bytePorByte = [Text.Encoding]::GetEncoding(28591)   # ISO-8859-1: 1 byte = 1 caractere

function Tem-Marca([string]$caminho, [string]$marca) {
    if (-not (Test-Path -LiteralPath $caminho)) { return $false }
    $conteudo = $bytePorByte.GetString([IO.File]::ReadAllBytes($caminho))
    $emUtf8 = $bytePorByte.GetString([Text.Encoding]::UTF8.GetBytes($marca))
    if ($conteudo.Contains($emUtf8)) { return $true }
    $emUtf16 = $bytePorByte.GetString([Text.Encoding]::Unicode.GetBytes($marca))
    return $conteudo.Contains($emUtf16)
}

function Mostrar-Marcadores([string]$pasta) {
    $faltando = 0
    foreach ($m in $marcadores) {
        $caminho = Join-Path $pasta $m.Arquivo
        if (Tem-Marca $caminho $m.Marca) {
            Write-Host ("  OK      {0,-26} {1}" -f $m.Marca, $m.Porque)
        } else {
            Write-Host ("  AUSENTE {0,-26} {1}" -f $m.Marca, $m.Porque) -ForegroundColor Yellow
            $faltando++
        }
    }
    return $faltando
}

Write-Host "destino ... $Destino"
if (-not (Test-Path -LiteralPath $Destino)) { throw "Pasta de destino nao existe: $Destino" }

if ($Conferir) {
    Write-Host ''
    Write-Host 'O que esta instalado agora (nada foi alterado):'
    $faltando = Mostrar-Marcadores $Destino
    Write-Host ''
    if ($faltando -gt 0) { Write-Host "$faltando marcador(es) ausente(s): o desktop esta atrasado." }
    else { Write-Host 'Todos os marcadores presentes.' }
    exit 0
}

# ------------------------------------------------------------------ 1. o aplicativo esta aberto?

# Os dois: a tela e o coletor que ela inicia. Qualquer um dos dois segura os DLLs.
$abertos = @(Get-Process -Name 'FugaTES', 'FugaTES.HostIntegracao' -ErrorAction SilentlyContinue)
if ($abertos.Count -gt 0) {
    $nomes = ($abertos | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
    throw "Feche o aplicativo antes de publicar. Em execucao: $nomes. Nada foi alterado."
}

# ------------------------------------------------------------------------------- 2. gerar o novo

$staging = Join-Path ([IO.Path]::GetTempPath()) ("fugates-desktop-" + [Guid]::NewGuid().ToString('N'))
Write-Host ''
Write-Host 'compilando a publicacao...'
& dotnet publish (Join-Path $raiz 'src\FugaTES.WinForms\FugaTES.WinForms.csproj') -c Release -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou com codigo $LASTEXITCODE. Nada foi alterado." }

# Antes de trocar: o que vai ser gerado tem de trazer as correcoes. Publicar um binario velho por
# cima de um velho seria uma operacao sem efeito com relatorio de sucesso - ja aconteceu hoje.
Write-Host ''
Write-Host 'conferindo o que foi compilado:'
if ((Mostrar-Marcadores $staging) -gt 0) {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    throw 'A publicacao compilada nao tem todos os marcadores. Nada foi alterado.'
}

# ----------------------------------------------------- 3. o conjunto de binarios tem de bater

$novos = Get-ChildItem -LiteralPath $staging -File | Where-Object { $_.Extension -in '.dll', '.exe', '.pdb' }
$atuais = Get-ChildItem -LiteralPath $Destino -File | Where-Object { $_.Extension -in '.dll', '.exe', '.pdb' }

# Binario que existe no novo e nao no instalado significa DEPENDENCIA NOVA - e aí o deps.json
# tambem precisaria mudar, o que este script nao faz de proposito. Parar e o certo.
$sobrando = Compare-Object $novos.Name $atuais.Name | Where-Object { $_.SideIndicator -eq '<=' }
if ($sobrando) {
    $lista = ($sobrando | ForEach-Object { $_.InputObject }) -join ', '
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    throw "A publicacao traz binario que nao existe na pasta instalada: $lista. Isso indica dependencia nova, e o deps.json tambem precisaria mudar. Nada foi alterado."
}

# -------------------------------------------------------------------- 4. copia de seguranca

$backup = "$Destino.bak-" + (Get-Date -Format 'yyyyMMdd_HHmmss')
Write-Host ''
Write-Host "copia de seguranca em $backup"
Copy-Item -LiteralPath $Destino -Destination $backup -Recurse -Force

# ----------------------------------------------------------------------------- 5. trocar

try {
    foreach ($arquivo in $novos) {
        Copy-Item -LiteralPath $arquivo.FullName -Destination (Join-Path $Destino $arquivo.Name) -Force
    }
    Write-Host "$($novos.Count) binario(s) trocado(s). Nenhum .json foi tocado."
}
catch {
    Write-Host 'FALHOU no meio da troca. Restaurando a copia de seguranca.' -ForegroundColor Red
    Copy-Item -LiteralPath (Join-Path $backup '*') -Destination $Destino -Recurse -Force
    throw
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

# --------------------------------------------------------- 6. conferir o que FICOU na pasta

Write-Host ''
Write-Host 'conferindo o que ficou instalado:'
$faltando = Mostrar-Marcadores $Destino
if ($faltando -gt 0) {
    Write-Host 'Marcador ausente DEPOIS da troca. Restaurando a copia de seguranca.' -ForegroundColor Red
    Copy-Item -LiteralPath (Join-Path $backup '*') -Destination $Destino -Recurse -Force
    throw 'Publicacao revertida.'
}

Write-Host ''
Write-Host 'Publicado. Os marcadores foram conferidos DENTRO dos binarios instalados.'
Write-Host "Para voltar:  Copy-Item -LiteralPath '$backup\*' -Destination '$Destino' -Recurse -Force"
