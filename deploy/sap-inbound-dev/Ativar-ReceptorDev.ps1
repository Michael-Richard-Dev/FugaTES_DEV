<#
    R166 - Ativacao do receptor SAP DEV em UM comando, a partir das definicoes empresariais.

    Este script NAO inventa valor nenhum: tudo que e especifico da homologacao entra por parametro,
    validado estritamente. Enquanto a Hera nao fornecer os valores, nada aqui roda.

    O template deploy/sap-inbound-dev/appsettings.receiver.dev.json PERMANECE com as pendencias:
    a configuracao ativada e gerada a parte, de modo que o pacote versionado continua fail-closed
    e os testes que provam isso continuam valendo.

    O que faz, nesta ordem, parando em qualquer divergencia:
      1  valida os parametros (forma do WindowId, janela <= 1 dia, parceiro/material/centro)
      2  confere a credencial Basic na VM (gera se faltar, com o usuario acordado)
      3  gera a configuracao ativada, sem nenhum <<PENDENTE>>
      4  reempacota e calcula o SHA
      5  chama Instalar-ReceptorDev.ps1, que ja traz backup, verificacao e rollback
      6  confere o resultado e diz exatamente o que ainda falta para o SAP alcancar o receptor

    O que NAO faz, por desenho: publicar rota corporativa, habilitar worker Guardian, executar POST
    SAP ou Guardian, tocar o ingress HML 8080, o 18761, o 18762 ou o 18763.

    Uso:
      .\Ativar-ReceptorDev.ps1 `
         -WindowId <32 hex> -AuthorizationReference <ref> `
         -NotBeforeUtc 2026-10-29T12:00:00Z -UntilUtc 2026-10-29T20:00:00Z `
         -Parceiro 0000500123 -Material 000000000000100045 -UsuarioBasic <usuario acordado>

      .\Ativar-ReceptorDev.ps1 -Conferir     # valida tudo e NAO instala
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WindowId,
    [Parameter(Mandatory = $true)][string]$AuthorizationReference,
    [Parameter(Mandatory = $true)][string]$NotBeforeUtc,
    [Parameter(Mandatory = $true)][string]$UntilUtc,
    [Parameter(Mandatory = $true)][string]$Parceiro,
    [Parameter(Mandatory = $true)][string]$Material,
    [Parameter(Mandatory = $true)][string]$UsuarioBasic,
    [string]$Centro = '3011',
    [int]$Porta = 18764,
    # LoopbackBasicHttps (padrao) exige X-Forwarded-Proto=https, alem do Basic e do peer loopback.
    # LoopbackOnly dispensa APENAS a exigencia de https, mantendo Basic e peer loopback. Os dois
    # modos sao os unicos aceitos por SapContractInboundProductiveComposition; nenhum e inventado
    # aqui. LoopbackOnly e o modo honesto quando o ingresso comprovadamente NAO tem TLS - usa-lo
    # evita declarar https onde o trafego nao foi protegido.
    [ValidateSet('LoopbackBasicHttps', 'LoopbackOnly')]
    [string]$SecurityMode = 'LoopbackBasicHttps',
    [switch]$Conferir
)

$ErrorActionPreference = 'Stop'
$aqui = $PSScriptRoot
$repo = Split-Path (Split-Path $aqui -Parent) -Parent
$template = Join-Path $aqui 'appsettings.receiver.dev.json'
$staging = Join-Path $env:TEMP ('r166_ativacao_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))

$estado = [ordered]@{}
function Veredito([string]$chave, [string]$valor, [string]$detalhe = '') {
    $estado[$chave] = $valor
    $cor = if ($valor -like 'PASS*') { 'Green' } elseif ($valor -like 'BLOCKED*') { 'Red' } else { 'Yellow' }
    Write-Host ("{0,-28} {1}" -f $chave, $valor) -ForegroundColor $cor
    if ($detalhe) { Write-Host ("  " + $detalhe) }
}
function Resumo { Write-Host ''; Write-Host '=== RESUMO ===' -ForegroundColor Cyan
    $estado.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-28} {1}" -f $_.Key, $_.Value) } }
function Abortar([string]$chave, [string]$motivo) { Veredito $chave 'BLOCKED' $motivo; Resumo; exit 1 }

Write-Host '=== R166 - ativacao do receptor SAP DEV ===' -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------- 1) parametros
if ($WindowId -notmatch '^[0-9a-f]{32}$') {
    Abortar 'PARAMETROS' 'WindowId deve ser um GUID no formato "N": 32 caracteres hexadecimais minusculos, sem hifens'
}
if ([string]::IsNullOrWhiteSpace($AuthorizationReference) -or $AuthorizationReference.Length -gt 120 -or
    $AuthorizationReference -match '<<PENDENTE') {
    Abortar 'PARAMETROS' 'AuthorizationReference ausente ou invalida; ela vem da autorizacao da Hera e nao e inventada aqui'
}
$inicio = [datetimeoffset]::MinValue; $fim = [datetimeoffset]::MinValue
if (-not [datetimeoffset]::TryParse($NotBeforeUtc, [ref]$inicio)) { Abortar 'PARAMETROS' "NotBeforeUtc invalido: $NotBeforeUtc" }
if (-not [datetimeoffset]::TryParse($UntilUtc, [ref]$fim))        { Abortar 'PARAMETROS' "UntilUtc invalido: $UntilUtc" }
if ($fim -le $inicio) { Abortar 'PARAMETROS' 'UntilUtc precisa ser posterior a NotBeforeUtc' }
if (($fim - $inicio).TotalDays -gt 1) { Abortar 'PARAMETROS' 'a janela R57 nao pode exceder 1 dia' }
foreach ($par in @(@{n='Parceiro';v=$Parceiro}, @{n='Material';v=$Material}, @{n='Centro';v=$Centro})) {
    if ([string]::IsNullOrWhiteSpace($par.v) -or $par.v -notmatch '^[A-Za-z0-9_.-]{1,40}$') {
        Abortar 'PARAMETROS' ("{0} invalido: use 1 a 40 caracteres [A-Za-z0-9_.-]" -f $par.n)
    }
}
if ($UsuarioBasic -notmatch '^[A-Za-z0-9_.-]{3,64}$') { Abortar 'PARAMETROS' 'UsuarioBasic invalido: 3 a 64 caracteres [A-Za-z0-9_.-]' }
foreach ($reservada in @(8080, 18761, 18762, 18763, 5432, 5433, 5434)) {
    if ($Porta -eq $reservada) { Abortar 'PARAMETROS' "porta $Porta pertence a um servico existente" }
}
Veredito 'PARAMETROS' 'PASS' ("janela {0:yyyy-MM-dd HH:mm}Z -> {1:yyyy-MM-dd HH:mm}Z | centro {2} | parceiro {3} | material {4}" -f `
    $inicio.UtcDateTime, $fim.UtcDateTime, $Centro, $Parceiro, $Material)

# ---------------------------------------------------------------- 2) credencial Basic
# Em -Conferir nada e gerado: so se olha o estado. Gerar credencial e efeito colateral real e nao
# pertence a um ensaio.
if ($Conferir) {
    & (Join-Path $aqui 'Preparar-CredencialBasic.ps1') -Conferir
    if ($LASTEXITCODE -ne 0) { Abortar 'BASIC_CREDENTIAL' 'nao foi possivel conferir a credencial Basic na VM' }
    Veredito 'BASIC_CREDENTIAL' 'CONFERENCIA' 'estado apenas consultado; nada foi gerado'
}
else {
    & (Join-Path $aqui 'Preparar-CredencialBasic.ps1') -Usuario $UsuarioBasic
    if ($LASTEXITCODE -ne 0) { Abortar 'BASIC_CREDENTIAL' 'nao foi possivel preparar a credencial Basic na VM' }
    Veredito 'BASIC_CREDENTIAL' 'PASS' 'presente na VM, modo 600; o segredo nao passa por aqui'
}

# ---------------------------------------------------------------- 3) configuracao ativada
if (-not (Test-Path $template)) { Abortar 'CONFIGURACAO' "template ausente: $template" }
$texto = Get-Content $template -Raw

$substituicoes = @(
    @{ de = '"<<PENDENTE_WINDOWID_GUID_N_APROVADO_HERA>>"'; para = ('"{0}"' -f $WindowId) },
    @{ de = '"<<PENDENTE_REFERENCIA_AUTORIZACAO_HERA>>"';   para = ('"{0}"' -f $AuthorizationReference) },
    @{ de = '"NotBeforeUtc": null';                         para = ('"NotBeforeUtc": "{0:yyyy-MM-ddTHH:mm:ssZ}"' -f $inicio.UtcDateTime) },
    @{ de = '"UntilUtc": null';                             para = ('"UntilUtc": "{0:yyyy-MM-ddTHH:mm:ssZ}"' -f $fim.UtcDateTime) },
    @{ de = '"AllowedSapPartners": []';                     para = ('"AllowedSapPartners": ["{0}"]' -f $Parceiro) },
    @{ de = '"AllowedSapMaterials": []';                    para = ('"AllowedSapMaterials": ["{0}"]' -f $Material) },
    @{ de = '"Centro": "3011"';                             para = ('"Centro": "{0}"' -f $Centro) },
    @{ de = '"Urls": "http://127.0.0.1:18764"';             para = ('"Urls": "http://127.0.0.1:{0}"' -f $Porta) },
    @{ de = '"SecurityMode": "LoopbackBasicHttps"';          para = ('"SecurityMode": "{0}"' -f $SecurityMode) }
)
foreach ($s in $substituicoes) {
    $ocorrencias = ([regex]::Escape($s.de) | ForEach-Object { ([regex]$_).Matches($texto).Count })
    if ($ocorrencias -ne 1) { Abortar 'CONFIGURACAO' ("padrao esperado nao encontrado exatamente uma vez no template: {0} (ocorrencias={1})" -f $s.de, $ocorrencias) }
    $texto = $texto.Replace($s.de, $s.para)
}
# A janela so e habilitada DEPOIS de todos os valores estarem no lugar.
$blocoAntes = '"AcmInboundWindow": {' + [Environment]::NewLine + '      "Enabled": false,'
$blocoDepois = '"AcmInboundWindow": {' + [Environment]::NewLine + '      "Enabled": true,'
if (([regex][regex]::Escape($blocoAntes)).Matches($texto).Count -ne 1) {
    Abortar 'CONFIGURACAO' 'nao foi possivel localizar o bloco AcmInboundWindow para habilitar a janela'
}
$texto = $texto.Replace($blocoAntes, $blocoDepois)

# Padrao ESTRITO, igual ao do instalador: casa marcadores reais, nao a prosa do comentario
# _FAIL_CLOSED, que cita "<<PENDENTE...>>" ao explicar a regra.
$restantes = ([regex]'<<PENDENTE[A-Z_0-9]*>>').Matches($texto) | ForEach-Object { $_.Value } | Select-Object -Unique
if ($restantes.Count -gt 0) {
    Abortar 'CONFIGURACAO' ("ainda restam pendencias na configuracao ativada:`n  " + ($restantes -join "`n  "))
}
try { $null = $texto | ConvertFrom-Json } catch { Abortar 'CONFIGURACAO' ("a configuracao ativada nao e JSON valido: " + $_.Exception.Message) }

New-Item -ItemType Directory -Path $staging -Force | Out-Null
$ativado = Join-Path $staging 'appsettings.receiver.dev.json'
[IO.File]::WriteAllText($ativado, (($texto -replace "`r`n", "`n") -replace "`n", "`r`n"), (New-Object Text.UTF8Encoding $true))
Veredito 'CONFIGURACAO' 'PASS' ("ativada em {0}; o template do repositorio segue com as pendencias" -f $ativado)

# O template versionado NAO pode ter sido tocado.
if (([regex]'<<PENDENTE[A-Z_0-9]*>>').Matches((Get-Content $template -Raw)).Count -lt 2) {
    Abortar 'TEMPLATE_PRESERVADO' 'o template do repositorio perdeu as pendencias; isso quebraria o fail-closed'
}
Veredito 'TEMPLATE_PRESERVADO' 'PASS' 'o template versionado continua fail-closed'

# ---------------------------------------------------------------- 4) pacote
$pacoteBase = Join-Path $repo '.artifacts\R166_DEV_IDENTITIES\FUGATES_SAP_INBOUND_RECEIVER_DEV.zip'
if (-not (Test-Path $pacoteBase)) { Abortar 'PACOTE' "pacote base ausente: $pacoteBase" }
$conteudo = Join-Path $staging 'pacote'
Expand-Archive -Path $pacoteBase -DestinationPath $conteudo -Force
Copy-Item $ativado (Join-Path $conteudo 'appsettings.receiver.dev.json') -Force
$pacoteAtivo = Join-Path $staging 'FUGATES_SAP_INBOUND_RECEIVER_DEV_ATIVADO.zip'
Compress-Archive -Path (Join-Path $conteudo '*') -DestinationPath $pacoteAtivo -CompressionLevel Optimal
$sha = (Get-FileHash $pacoteAtivo -Algorithm SHA256).Hash
Veredito 'PACOTE' 'PASS' ("{0} | {1}" -f $sha.Substring(0, 16), $pacoteAtivo)

if ($Conferir) {
    Veredito 'INSTALACAO' 'CONFERENCIA' 'modo -Conferir: tudo validado, nada instalado'
    Resumo
    Write-Host ''
    Write-Host 'Para instalar de fato, repita sem -Conferir.' -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------- 5) instalacao
& (Join-Path $aqui 'Instalar-ReceptorDev.ps1') -Pacote $pacoteAtivo -Porta $Porta -ShaPacote $sha
if ($LASTEXITCODE -ne 0) { Abortar 'INSTALACAO' 'a instalacao falhou; o instalador ja aplicou o rollback e preservou a instalacao anterior' }
Veredito 'INSTALACAO' 'PASS' ("receptor ativo em 127.0.0.1:{0}, janela R57 aberta ate {1:yyyy-MM-dd HH:mm}Z" -f $Porta, $fim.UtcDateTime)

Resumo
Write-Host ''
Write-Host 'O receptor esta no ar e ACEITA o contrato autorizado. Falta apenas o caminho ate ele:' -ForegroundColor Yellow
Write-Host '  - publicar a rota DEV (proposta em nginx-fugates-dev-sap.conf.PROPOSTA), com TLS real,' -ForegroundColor Yellow
Write-Host '    X-Forwarded-Proto https e o header Authorization preservado;' -ForegroundColor Yellow
Write-Host '  - repontar o emissor SAP/CPI para essa rota e entregar-lhe a credencial Basic.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Depois do primeiro envio, confira no FugaTES DEV110: Integracoes -> Contratos ACM.' -ForegroundColor Green
