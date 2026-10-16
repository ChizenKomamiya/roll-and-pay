#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$backendPath = Join-Path $PSScriptRoot '../../backend'
Push-Location $backendPath
try {
    dotnet restore RollAndPay.Api.csproj
    if ($LASTEXITCODE -ne 0) { throw 'Backend restore failed.' }
    dotnet test tests/RollAndPay.Api.Tests.csproj --configuration Release --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
    dotnet publish RollAndPay.Api.csproj --configuration Release --no-restore --output (Join-Path $backendPath 'bin/ci-publish')
    if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }
    if (!(Test-Path -LiteralPath (Join-Path $backendPath 'bin/ci-publish/host.json'))) { throw 'Functions host.json is missing from the publish output.' }
    $unexpected = Get-ChildItem -LiteralPath (Join-Path $backendPath 'bin/ci-publish') -Recurse -Filter 'local.settings*.json'
    if ($unexpected) { throw 'Local configuration must not be included in the deployment.' }
    $testArtifacts = Get-ChildItem -LiteralPath (Join-Path $backendPath 'bin/ci-publish') -Recurse -File |
        Where-Object { $_.Name -match 'RollAndPay\.Api\.Tests|RollAndPay\.Seed|xunit|testhost|Moq|Castle\.Core' -or $_.FullName -match '[\\/]tests[\\/]' }
    if ($testArtifacts) { throw 'Test artifacts must not be included in the deployment.' }
}
finally { Pop-Location }
