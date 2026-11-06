# Cria C:\FugaTES_QAS120 a partir do C:\FugaTES_DEV110.
#
# POR QUE A PARTIR DO DEV. A mesma publicacao serve os dois ambientes; o que os distingue e o
# FugaTES.carrier.json ao lado do executavel (ver AmbienteDesktop). Entao o QAS nao e uma
# instalacao nova: e a copia de uma que funciona, com os perfis que TEM de diferir reescritos.
# E o mesmo desenho do instalar-servicos-qas.sh na VM - herdar, e trocar so o necessario.
#
# O QUE E REESCRITO, E POR QUE CADA UM:
#
#   FugaTES.carrier.json  Sap.BaseUrl e Sap.SapClient. E so isto que faz a janela SABER que e
#                         QAS120 - o titulo, o par banco+papel do SD e o AmbientesSap saem dai.
#
#   FugaTES.sd.json       Database e Username, que a ConexaoSdPostgreSql confere contra o
#                         ambiente da janela; CredentialTarget, para a credencial do QAS nao ser
#                         a mesma entrada do DEV no Gerenciador de Credenciais; e a porta LOCAL
#                         do tunel, que precisa ser outra - dois desktops abertos disputando a
#                         15433 e o caso que o proprio codigo chama de "tunel orfao que responde
#                         igual e sequestra".
#
# NENHUMA SENHA PASSA POR AQUI. A senha do PostgreSQL vive no Gerenciador de Credenciais do
# Windows; este script so escreve o NOME do alvo. Quem grava a senha e voce, com o cmdkey que ele
# imprime no fim.
#
# OS OUTROS PERFIS SAO COPIADOS COMO ESTAO - e o script DIZ quais deles continuam apontando para
# o DEV. MM e ACM nao sao escopo desta copia; deixa-los apontando para o DEV em silencio seria
# plantar exatamente o cruzamento que o dia inteiro fechou.
#
# Uso:  .\Criar-Desktop-QAS.ps1 conferir
#       .\Criar-Desktop-QAS.ps1 aplicar

[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('conferir', 'aplicar')][string]$Acao = 'conferir',
    [string]$Origem  = 'C:\FugaTES_DEV110',
    [string]$Destino = 'C:\FugaTES_QAS120'
)

$ErrorActionPreference = 'Stop'

$OrigemQas    = 'https://vhfufqs4ci.sap.fugacouros.com.br:44300'   # a MESMA string do AmbientesSap.Qas120
$SapQasClient = '120'
$BancoQas     = 'fugates_qas'
$PapelSdQas   = 'fugates_qas_sd_app'
$AlvoCredQas  = 'FugaTES/QAS120/PostgreSQL/SD'
$PortaTunelQas = 15434

if (-not (Test-Path -LiteralPath $Origem)) { throw "Origem nao existe: $Origem" }

# ESCREVER JSON SEM BOM, PORQUE O APLICATIVO LE OS BYTES CRUS.
#
# CarrierDesktopProfile.Load faz File.ReadAllBytes + JsonDocument.Parse(bytes). O Utf8JsonReader
# NAO aceita a marca de ordem de bytes: ele lanca, o catch vira
# CARRIER_DESKTOP_HML_PROFILE_INVALID, e o AmbienteDesktop devolve "ambiente nao identificado".
#
# "Set-Content -Encoding utf8" no PowerShell 5.1 escreve COM BOM. Em 06/11/2026 foi isso que fez a
# janela do QAS120 abrir dizendo que nao sabia em que ambiente estava - com o par correto dentro
# do arquivo. O mesmo -Encoding utf8 ja tinha posto um BOM no assunto de um commit horas antes, e
# eu nao liguei as duas coisas.
function Escrever-JsonSemBom([object]$objeto, [string]$caminho) {
    $texto = $objeto | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($caminho, $texto, [Text.UTF8Encoding]::new($false))
    # Conferir o EFEITO: os primeiros bytes tem de ser o proprio JSON.
    $b = [IO.File]::ReadAllBytes($caminho)
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) {
        throw "O arquivo $caminho saiu com BOM; o aplicativo nao conseguiria le-lo."
    }
}

# A URL DO SAP E DERIVADA, NAO DIGITADA.
#
# O carrier.json do DEV traz a URL COM CAMINHO
# (.../sap/opu/odata/sap/API_BUSINESS_PARTNER). O AmbienteDesktop compara so a autoridade
# (GetLeftPart(UriPartial.Authority)), entao um literal sem caminho ainda identificaria a janela
# como QAS120 - e a tela do carrier chamaria um endereco sem caminho nenhum. Passaria no titulo
# e falharia na chamada, que e a pior ordem possivel.
#
# Entao troca-se a AUTORIDADE e preserva-se o resto. Depois confere-se que a autoridade resultante
# e exatamente a do ambiente aprovado - deriva, e depois prova.
function Url-DoQas([string]$urlDev) {
    $u = [Uri]$urlDev
    $autoridadeQas = [Uri]$script:OrigemQas
    $novo = [UriBuilder]::new($u)
    $novo.Scheme = $autoridadeQas.Scheme
    $novo.Host   = $autoridadeQas.Host
    $novo.Port   = $autoridadeQas.Port
    $resultado = $novo.Uri
    if ($resultado.GetLeftPart([UriPartial]::Authority) -ne $script:OrigemQas) {
        throw ("A URL montada nao tem a autoridade do QAS120 aprovada.`n" +
               "  montada: " + $resultado.GetLeftPart([UriPartial]::Authority) + "`n" +
               "  exigida: " + $script:OrigemQas)
    }
    $resultado.AbsoluteUri.TrimEnd('/')
}

# As marcas do DEV que nao deveriam sobreviver num perfil do QAS. Nao sao segredo: sao nomes de
# banco, de papel e o host do SAP de desenvolvimento.
$MarcasDev = @('fugates_dev', 'fugates_sd_app', 'fugates_acm_hml_app', 'fugates_mm_app',
               'vhfufds4ci', '"SapClient": "110"', 'DEV110')

function Mostrar-PerfisComMarcaDev([string]$pasta) {
    $achados = @()
    Get-ChildItem -LiteralPath $pasta -Filter *.json -File |
        Where-Object { $_.Name -notmatch '\.(deps|runtimeconfig)\.json$' } |
        ForEach-Object {
            $texto = Get-Content -LiteralPath $_.FullName -Raw
            $marcas = $MarcasDev | Where-Object { $texto -match [regex]::Escape($_) }
            if ($marcas) { $achados += [pscustomobject]@{ Arquivo = $_.Name; Marcas = ($marcas -join ', ') } }
        }
    $achados
}

switch ($Acao) {

'conferir' {
    Write-Host '=============================================================='
    Write-Host ' O QUE EXISTE HOJE'
    Write-Host '=============================================================='
    Write-Host ("  origem ..... {0}  ({1} arquivos)" -f $Origem, (Get-ChildItem -LiteralPath $Origem -File).Count)
    if (Test-Path -LiteralPath $Destino) {
        Write-Host ("  destino .... {0}  JA EXISTE - o aplicar nao sobrescreve" -f $Destino)
    } else {
        Write-Host ("  destino .... {0}  nao existe (sera criado)" -f $Destino)
    }

    Write-Host ''
    Write-Host 'o ambiente que a janela de origem declara:'
    $c = Get-Content -LiteralPath (Join-Path $Origem 'FugaTES.carrier.json') -Raw | ConvertFrom-Json
    Write-Host ("   Sap.BaseUrl ..... {0}" -f $c.Sap.BaseUrl)
    Write-Host ("   Sap.SapClient ... {0}" -f $c.Sap.SapClient)

    $sd = Get-Content -LiteralPath (Join-Path $Origem 'FugaTES.sd.json') -Raw | ConvertFrom-Json
    Write-Host ''
    Write-Host 'o perfil de banco do SD na origem (sem senha: ela vive no Gerenciador de Credenciais):'
    Write-Host ("   Database ............. {0}" -f $sd.Database.Database)
    Write-Host ("   Username ............. {0}" -f $sd.Database.Username)
    Write-Host ("   CredentialTarget ..... {0}" -f $sd.Database.CredentialTarget)
    Write-Host ("   porta local do tunel . {0}" -f $sd.Database.Port)

    Write-Host ''
    Write-Host 'o que o aplicar escreveria no destino:'
    Write-Host ("   Sap.BaseUrl .......... {0}" -f (Url-DoQas $c.Sap.BaseUrl))
    Write-Host ("   Sap.SapClient ........ {0}" -f $SapQasClient)
    Write-Host ("   Database ............. {0}" -f $BancoQas)
    Write-Host ("   Username ............. {0}" -f $PapelSdQas)
    Write-Host ("   CredentialTarget ..... {0}" -f $AlvoCredQas)
    Write-Host ("   porta local do tunel . {0}   (outra, para nao disputar a {1} com o DEV)" -f $PortaTunelQas, $sd.Database.Port)

    Write-Host ''
    Write-Host 'perfis que seriam COPIADOS AINDA APONTANDO PARA O DEV:'
    $restantes = Mostrar-PerfisComMarcaDev $Origem |
        Where-Object { $true }   # TODOS, inclusive os que eu reescrevi - ver nota abaixo
    if (-not $restantes) {
        Write-Host '   nenhum'
    } else {
        $restantes | ForEach-Object { Write-Host ("   {0,-32} {1}" -f $_.Arquivo, $_.Marcas) }
        Write-Host ''
        Write-Host '   Estes ficam apontando para o DEV ate serem convertidos. No carrier.json e a'
        Write-Host '   APROVACAO (Origin, ApprovalReference) - ver a nota do commit; nos demais, o banco.'
    }
    Write-Host ''
    Write-Host 'Nada foi alterado. Para criar:  .\Criar-Desktop-QAS.ps1 aplicar'
}

'aplicar' {
    if (Test-Path -LiteralPath $Destino) {
        throw "$Destino ja existe. Apague-o a mao se quiser refazer - este script nao sobrescreve instalacao."
    }

    Write-Host ("== copiando {0} -> {1}" -f $Origem, $Destino)
    Copy-Item -LiteralPath $Origem -Destination $Destino -Recurse
    $n = (Get-ChildItem -LiteralPath $Destino -File -Recurse).Count
    Write-Host ("   {0} arquivos" -f $n)

    # CARRIER: e o unico arquivo que decide o ambiente da janela.
    $carrierPath = Join-Path $Destino 'FugaTES.carrier.json'
    $carrier = Get-Content -LiteralPath $carrierPath -Raw | ConvertFrom-Json
    $carrier.Sap.BaseUrl   = Url-DoQas $carrier.Sap.BaseUrl
    $carrier.Sap.SapClient = $SapQasClient
    Escrever-JsonSemBom $carrier $carrierPath
    Write-Host '== FugaTES.carrier.json: Sap.BaseUrl e Sap.SapClient trocados'

    # SD: banco, papel, alvo da credencial e porta local do tunel.
    $sdPath = Join-Path $Destino 'FugaTES.sd.json'
    $sd = Get-Content -LiteralPath $sdPath -Raw | ConvertFrom-Json
    $portaAntiga = $sd.Database.Port
    $sd.Database.Database         = $BancoQas
    $sd.Database.Username         = $PapelSdQas
    $sd.Database.CredentialTarget = $AlvoCredQas
    $sd.Database.Port             = $PortaTunelQas
    Escrever-JsonSemBom $sd $sdPath
    Write-Host '== FugaTES.sd.json: banco, papel, alvo da credencial e porta do tunel trocados'

    # A REESCRITA PRESERVOU O RESTO? ConvertTo-Json reordena, reescreve e pode perder o que
    # estiver alem da profundidade. Conferir o EFEITO, e nao supor que a serializacao deu certo:
    # comparar as folhas dos dois arquivos, menos as que eu troquei de proposito.
    function Folhas($obj, $prefixo = '') {
        $saida = @{}
        foreach ($p in $obj.PSObject.Properties) {
            $k = if ($prefixo) { "$prefixo.$($p.Name)" } else { $p.Name }
            if ($p.Value -is [pscustomobject]) { (Folhas $p.Value $k).GetEnumerator() | ForEach-Object { $saida[$_.Key] = $_.Value } }
            else { $saida[$k] = [string]$p.Value }
        }
        $saida
    }
    $trocadas = @('Sap.BaseUrl', 'Sap.SapClient', 'Database.Database', 'Database.Username',
                  'Database.CredentialTarget', 'Database.Port')
    $perdidas = @()
    foreach ($par in @(@{o='FugaTES.carrier.json'}, @{o='FugaTES.sd.json'})) {
        $antes  = Folhas (Get-Content -LiteralPath (Join-Path $Origem  $par.o) -Raw | ConvertFrom-Json)
        $depois = Folhas (Get-Content -LiteralPath (Join-Path $Destino $par.o) -Raw | ConvertFrom-Json)
        foreach ($k in $antes.Keys) {
            if ($k -in $trocadas) { continue }
            if (-not $depois.ContainsKey($k)) { $perdidas += "$($par.o): $k SUMIU" }
            elseif ($depois[$k] -ne $antes[$k]) { $perdidas += "$($par.o): $k mudou sem eu ter pedido" }
        }
    }
    if ($perdidas) {
        Write-Host ''
        Write-Host 'A REESCRITA DO JSON PERDEU OU MUDOU COISA:' -ForegroundColor Red
        $perdidas | ForEach-Object { Write-Host ("   " + $_) }
        Write-Host ("Apague {0} e me avise. Nao use esta pasta." -f $Destino)
        throw 'Reescrita de perfil nao preservou o conteudo.'
    }
    Write-Host '== o resto dos dois perfis sobreviveu a reescrita, campo por campo'

    Write-Host ''
    Write-Host '=============================================================='
    Write-Host ' FALTA VOCE'
    Write-Host '=============================================================='
    Write-Host '  1) Gravar a senha do papel do QAS no Gerenciador de Credenciais.'
    Write-Host '     A senha e a que voce definiu com \password; ela nao passa por este script.'
    Write-Host ''
    Write-Host ("     cmdkey /generic:{0} /user:{1} /pass" -f $AlvoCredQas, $PapelSdQas)
    Write-Host ''
    Write-Host '     (sem a senha na linha: o cmdkey pergunta, e ela nao fica no historico)'
    Write-Host ''
    Write-Host '  2) Abrir o desktop do QAS e conferir o TITULO da janela:'
    Write-Host ("     {0}\FugaTES.exe" -f $Destino)
    Write-Host '     Tem de dizer "FugaTES - QAS120". Se disser "ambiente nao identificado",'
    Write-Host '     o carrier.json nao casou com nenhum ambiente aprovado - me avise.'

    $restantes = Mostrar-PerfisComMarcaDev $Destino |
        Where-Object { $true }   # TODOS, inclusive os que eu reescrevi - ver nota abaixo
    if ($restantes) {
        Write-Host ''
        Write-Host '  ATENCAO: estes perfis foram copiados e CONTINUAM apontando para o DEV:'
        $restantes | ForEach-Object { Write-Host ("     {0,-32} {1}" -f $_.Arquivo, $_.Marcas) }
        Write-Host '     Fora do escopo desta copia. Nao use as telas deles'
        Write-Host '     nesta janela ate serem convertidos.'
    }
}

}
