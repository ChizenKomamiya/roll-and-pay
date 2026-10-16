#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repoPath
$sampleValues = @{
    ROLLPAY_API_CLIENT_ID = '11111111-1111-1111-1111-111111111111'
    ROLLPAY_SPA_CLIENT_ID = '22222222-2222-2222-2222-222222222222'
    ROLLPAY_ADMIN_OBJECT_ID = '33333333-3333-3333-3333-333333333333'
    ROLLPAY_BUDGET_START_DATE = '2026-10-01T00:00:00Z'
    ROLLPAY_BUDGET_EMAIL = 'validation@example.invalid'
}
$previous = @{}
try {
    pwsh -NoProfile -File ./infra/tests/Test-EntraScripts.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Entra offline tests failed.' }
    pwsh -NoProfile -File ./infra/tests/Test-BackendDeploymentIdentity.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Backend deployment identity offline tests failed.' }
    az bicep install --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Bicep installation failed.' }
    New-Item -ItemType Directory -Path infra/build -Force | Out-Null
    az bicep build --file infra/main.bicep --outfile infra/build/main.json
    if ($LASTEXITCODE -ne 0) { throw 'Infrastructure compilation failed.' }
    foreach ($name in $sampleValues.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $sampleValues[$name])
    }
    az bicep build-params --file infra/prod.bicepparam --outfile infra/build/prod.validation.parameters.json
    if ($LASTEXITCODE -ne 0) { throw 'Parameter compilation failed.' }
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    Pop-Location
}
