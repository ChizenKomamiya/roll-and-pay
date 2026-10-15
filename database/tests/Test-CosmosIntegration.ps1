#Requires -Version 7.0
[CmdletBinding()]
param(
    [uri] $Endpoint = 'https://localhost:8081/'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$Endpoint.IsLoopback -or $Endpoint.Scheme -ne 'https' -or $Endpoint.UserInfo -or
    $Endpoint.Query -or $Endpoint.Fragment -or $Endpoint.AbsolutePath -ne '/') {
    throw 'Only an HTTPS loopback Cosmos emulator endpoint is allowed.'
}
if (!$env:ROLLPAY_EMULATOR_KEY) { throw 'Set ROLLPAY_EMULATOR_KEY to the local emulator key; do not use production credentials.' }
$previousRun = $env:ROLLPAY_RUN_COSMOS_TESTS
$previousEndpoint = $env:ROLLPAY_EMULATOR_ENDPOINT
$backendPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../backend'))
Push-Location $backendPath
try {
    $env:ROLLPAY_RUN_COSMOS_TESTS = '1'
    $env:ROLLPAY_EMULATOR_ENDPOINT = $Endpoint.AbsoluteUri
    dotnet restore RollAndPay.Api.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet test tests/RollAndPay.Api.Tests.csproj --configuration Release --filter 'Category=CosmosIntegration' --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw 'Cosmos emulator integration tests failed.' }
}
finally {
    $env:ROLLPAY_RUN_COSMOS_TESTS = $previousRun
    $env:ROLLPAY_EMULATOR_ENDPOINT = $previousEndpoint
    Pop-Location
}
