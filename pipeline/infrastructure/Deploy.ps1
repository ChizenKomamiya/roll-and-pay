#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('what-if', 'apply')][string] $Operation)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($name in 'ROLLPAY_AZURE_SUBSCRIPTION_ID','ROLLPAY_API_CLIENT_ID','ROLLPAY_SPA_CLIENT_ID','ROLLPAY_ADMIN_OBJECT_ID','ROLLPAY_BUDGET_START_DATE','ROLLPAY_BUDGET_EMAIL') {
    if (![Environment]::GetEnvironmentVariable($name)) { throw "Missing environment setting: $name" }
}
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repoPath
try {
    az bicep install --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Bicep installation failed.' }
    $common = @('--name','rollpay-prod','--location','japaneast','--template-file','infra/main.bicep','--parameters','infra/prod.bicepparam','--subscription',$env:ROLLPAY_AZURE_SUBSCRIPTION_ID,'--only-show-errors')
    az deployment sub validate @common --output none
    if ($LASTEXITCODE -ne 0) { throw 'Azure infrastructure validation failed.' }
    # ResourceIdOnly avoids logging contact addresses and configuration values.
    az deployment sub what-if @common --result-format ResourceIdOnly
    if ($LASTEXITCODE -ne 0) { throw 'Infrastructure what-if failed.' }
    if ($Operation -eq 'apply') {
        az deployment sub create @common --query properties.provisioningState --output tsv
        if ($LASTEXITCODE -ne 0) { throw 'Infrastructure deployment failed. Verify Azure deployment state before retrying.' }
    }
}
finally { Pop-Location }
