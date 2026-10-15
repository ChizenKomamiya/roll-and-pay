#Requires -Version 7.0
<#
.SYNOPSIS
指定テナントにログイン中のユーザーObject IDを取得する（Azureへの書込みなし）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid] $TenantId,
    [string] $SubscriptionId,
    [switch] $SetEnvironmentVariables,
    [ValidatePattern('\.local\.json$')][string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$script:RollPaySubscriptionId = $SubscriptionId
. (Join-Path $PSScriptRoot 'AzureCli.Common.ps1')

$context = Get-RollPayUserContext -ExpectedTenantId $TenantId
$result = [pscustomobject]$context
if ($SetEnvironmentVariables) {
    $env:ROLLPAY_ADMIN_OBJECT_ID = $context.AdministratorObjectId
}
Write-RollPayLocalResult -Result $result -OutputPath $OutputPath
