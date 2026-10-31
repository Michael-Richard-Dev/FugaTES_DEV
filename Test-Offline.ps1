$ErrorActionPreference = 'Stop'
$previousPackageRoot = $env:R165R_PACKAGE_ROOT
$previousPhysicalProfile = $env:FUGATES_CARRIER_TEST_PG_PROFILE
$env:R165R_PACKAGE_ROOT = Join-Path $PSScriptRoot 'test-fixtures\R165R_DISABLED_PROFILE'
$env:FUGATES_CARRIER_TEST_PG_PROFILE = $null
Push-Location $PSScriptRoot
try {
    & dotnet test FugaTES.slnx -c Release --no-restore --logger trx --results-directory TestResults @args
    if($LASTEXITCODE){throw "OFFLINE_TESTS_FAILED: $LASTEXITCODE"}
} finally {
    Pop-Location
    $env:R165R_PACKAGE_ROOT = $previousPackageRoot
    $env:FUGATES_CARRIER_TEST_PG_PROFILE = $previousPhysicalProfile
}
