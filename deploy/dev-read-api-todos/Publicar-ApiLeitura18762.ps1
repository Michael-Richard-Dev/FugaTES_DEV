<#
    R160 - Atualizacao CONTROLADA da API privada GET-only 18762 (suporte ao seletor TODOS).

    BLOQUEADO POR PADRAO. Sem -AutorizacaoHera o script nao envia nada: apenas valida o pacote,
    confere o estado remoto e imprime o plano. Nenhuma publicacao ocorre sem autorizacao fisica
    especifica da Hera.

    Preserva integralmente:
      - o tunel local 18761 (o script nao o usa nem o encerra);
      - o servico autenticado de comandos 18763;
      - a instalacao atual da API, que vira o ponto de rollback.

    NAO altera PostgreSQL, NAO envia POST SAP/Guardian, NAO habilita worker, NAO cria janela.

    Uso:
      # 1) ensaio, sem autorizacao: valida e imprime o plano
      powershell -NoProfile -ExecutionPolicy Bypass -File .\Publicar-ApiLeitura18762.ps1 -Pacote <zip>
      # 2) publicacao, somente com autorizacao explicita da Hera
      ... -Pacote <zip> -AutorizacaoHera 'FUGATES-...-AUTORIZACAO'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Pacote,
    [string]$AutorizacaoHera,
    [string]$ShaEsperado = '836A9D58826E1460E3CB8589B92D98A2484C2BBFAA4CE9FCF7605A0D3F27D703',
    [string]$Endpoint = 'michael@192.168.17.40',
    [string]$RaizRemota = '/home/michael/.fugates-unified-dev-read',
    [string]$InstalacaoAtual = '20261027_ECF854604610',
    [string]$IdentityFile = 'C:\ProgramData\FugaTES\agentic\ssh\fugates-agentic-nt-ti-256',
    [string]$KnownHosts = 'C:\Users\TI\.ssh\known_hosts'
)

$ErrorActionPreference = 'Stop'

function Falhar([string]$motivo) { Write-Output 'READ_API_18762_RELEASE=BLOCKED'; Write-Output ("  " + $motivo); exit 1 }

# ---------- 1) validacao local do pacote ----------
if (-not (Test-Path $Pacote)) { Falhar "pacote ausente: $Pacote" }
$sha = (Get-FileHash $Pacote -Algorithm SHA256).Hash
if ($sha -ne $ShaEsperado.ToUpper()) { Falhar "SHA256 do pacote nao confere.`n  esperado: $ShaEsperado`n  obtido  : $sha" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Pacote))
try {
    $nomes = @($zip.Entries | ForEach-Object { $_.FullName })
    if ($nomes -notcontains 'MANIFEST.json') { Falhar 'pacote sem MANIFEST.json' }
    foreach ($proibido in @('appsettings.json', 'appsettings.Development.json')) {
        if ($nomes -contains $proibido) { Falhar "pacote contem $proibido; o modo GET-only nao usa appsettings" }
    }
    foreach ($n in $nomes) {
        if ($n -match 'KEY|BEARER|pgpass|\.pem$|id_rsa|id_ed25519') { Falhar "pacote contem possivel credencial: $n" }
    }
    $entrada = $zip.GetEntry('MANIFEST.json')
    $leitor = New-Object IO.StreamReader($entrada.Open())
    try { $manifesto = $leitor.ReadToEnd() | ConvertFrom-Json } finally { $leitor.Dispose() }
}
finally { $zip.Dispose() }

if ($manifesto.mode -ne '--dev-unified-read-only') { Falhar "modo inesperado: $($manifesto.mode)" }
if ([int]$manifesto.port -ne 18762) { Falhar "porta inesperada: $($manifesto.port)" }
if ([int]$manifesto.databasePort -ne 5433) { Falhar "banco inesperado: $($manifesto.databasePort)" }
if ($manifesto.credentialsIncluded) { Falhar 'manifesto declara credenciais embutidas' }

Write-Output ("PACOTE={0}" -f (Split-Path $Pacote -Leaf))
Write-Output ("SHA256={0}" -f $sha)
Write-Output ("MODO={0}  PORTA={1}  BANCO={2}" -f $manifesto.mode, $manifesto.port, $manifesto.databasePort)
Write-Output ("ARQUIVOS={0}" -f @($manifesto.files).Count)
Write-Output ("ROLLBACK_PARA={0}/{1}" -f $RaizRemota, $InstalacaoAtual)
Write-Output 'TUNEL_18761=NAO_UTILIZADO_POR_ESTE_SCRIPT'
Write-Output 'SERVICO_COMANDOS_18763=PRESERVADO'

# ---------- 2) porta de autorizacao ----------
if ([string]::IsNullOrWhiteSpace($AutorizacaoHera)) {
    Write-Output 'READ_API_18762_RELEASE=READY_AWAITING_HERA_AUTHORIZATION'
    Write-Output '  Pacote validado. NENHUMA publicacao foi executada.'
    Write-Output '  Plano, quando autorizado:'
    Write-Output ("    1. scp do pacote para {0}/<sha>.zip" -f $RaizRemota)
    Write-Output '    2. conferir sha256sum remoto'
    Write-Output '    3. extrair em diretorio NOVO com carimbo de data (a instalacao atual permanece intacta)'
    Write-Output '    4. iniciar: /usr/bin/dotnet <novo>/FugaTES.Callback.Http.dll --dev-unified-read-only'
    Write-Output '    5. confirmar listener 127.0.0.1:18762 e GET /dev/read/identity'
    Write-Output '    6. encerrar o PID anterior somente apos a confirmacao'
    Write-Output '    ROLLBACK: reiniciar a instalacao anterior e encerrar o PID novo; nada e removido.'
    exit 0
}

# ---------- 3) publicacao autorizada ----------
foreach ($arquivo in @($IdentityFile, $KnownHosts)) {
    if (-not (Test-Path $arquivo)) { Falhar "pre-requisito SSH ausente: $arquivo" }
}
Write-Output ("AUTORIZACAO={0}" -f $AutorizacaoHera)
Write-Output 'READ_API_18762_RELEASE=AUTHORIZED_EXECUTION_REQUIRES_OPERATOR_TERMINAL'
Write-Output '  A transferencia e a troca de processo exigem o terminal do operador, com a chave SSH'
Write-Output '  e a senha, se houver, digitadas somente la. Este script nao captura credenciais e'
Write-Output '  nao executa a publicacao de forma nao assistida.'
exit 0
