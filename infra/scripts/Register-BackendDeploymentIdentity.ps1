#Requires -Version 7.0
<#
.SYNOPSIS
GitHub Actionsから既存Function Appへ配置する専用マネージドIDを準備する。
.DESCRIPTION
ユーザー割り当てマネージドID、main限定のOIDC連携、Function App単体の
Website Contributorを作成する。パスワードは作らない。-WhatIfは読取りのみ。
同じ管理タグ・配置先のIDを再利用する。旧版が登録した同じmainのSubjectだけを
数値ID入りの形式へ更新し、管理外の同名IDやその他の信頼設定では停止する。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][guid] $TenantId,
    [Parameter(Mandatory)][guid] $SubscriptionId,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9-]{0,59}$')][string] $FunctionAppName,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string] $ResourceGroupName = 'rg-rollpay-prod-jpe',
    [ValidatePattern('^[a-zA-Z0-9_-]{3,128}$')][string] $IdentityName = 'id-rollpay-backend-deploy-prod',
    [ValidatePattern('\.local\.json$')][string] $OutputPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AzureCli.Common.ps1')
$subscription = $SubscriptionId.ToString()
$repository = 'ChizenKomamiya/roll-and-pay'
$issuer = 'https://token.actions.githubusercontent.com'
# このリポジトリで発行されるimmutable Subject（GitHubのowner ID / repository ID）。
# 2026-07-15以降の新規リポジトリは名前だけの旧形式を使用しない。
$subject = 'repo:ChizenKomamiya@33175412/roll-and-pay@1418381637:ref:refs/heads/main'
$legacySubject = "repo:${repository}:ref:refs/heads/main"
$audience = 'api://AzureADTokenExchange'
$credentialName = 'github-main'
# Website Contributor。権限のスコープは下記のFunction App単体に限定する。
$roleId = 'de139f84-1756-47ae-9be6-808fbbe84772'
$functionScope = "/subscriptions/$subscription/resourceGroups/$ResourceGroupName/providers/Microsoft.Web/sites/$FunctionAppName"
$identityScope = "/subscriptions/$subscription/resourceGroups/$ResourceGroupName/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$IdentityName"
$expectedTags = @{
    'rollpay-managed' = 'backend-deploy-bootstrap-v1'
    'rollpay-repository' = $repository
    'rollpay-function-app' = $FunctionAppName
}
$targetArguments = @('--subscription', $subscription, '--resource-group', $ResourceGroupName)

$account = Invoke-RollPayAzureJson -Arguments @('account', 'show')
if ($account['environmentName'] -ne 'AzureCloud') { throw 'このスクリプトはAzureパブリッククラウド用です。' }
if ([string]$account['tenantId'] -ne $TenantId.ToString()) { throw 'ログイン先のテナントが指定値と違います。az login --tenant <TenantId> を確認してください。' }
if ([string]$account['id'] -ne $subscription) { throw '現在選択されているサブスクリプションが指定値と違います。先に az account set --subscription <SubscriptionId> を実行してください。' }

$function = Invoke-RollPayAzureJson -Arguments (@('functionapp', 'show', '--name', $FunctionAppName, '--query', '{id:id,name:name,kind:kind,location:location}') + $targetArguments)
if (-not $function -or $function['id'] -ne $functionScope -or @(([string]$function['kind']).Split(',')) -notcontains 'functionapp') {
    throw '指定した既存Function Appを確認できません。配置先を確認してください。'
}

$identities = @(Invoke-RollPayAzureJson -Arguments (@('identity', 'list') + $targetArguments))
$matchingIdentities = @($identities | Where-Object { $_['name'] -eq $IdentityName })
if ($matchingIdentities.Count -gt 1) { throw '同名IDが複数あります。自動選択しません。' }
$identity = if ($matchingIdentities.Count) { $matchingIdentities[0] } else { $null }
$credentials = @()
$updateLegacySubject = $false
if ($identity) {
    if ($identity['id'] -ne $identityScope -or [string]$identity['tenantId'] -ne $TenantId.ToString()) { throw '既存IDの配置先またはテナントが不一致です。' }
    foreach ($key in $expectedTags.Keys) {
        if (-not $identity['tags'] -or $identity['tags'][$key] -ne $expectedTags[$key]) {
            throw '管理外または配置先が異なる同名IDがあります。既存IDを確認するか、別のIdentityNameを指定してください。'
        }
    }
    $credentials = @(Invoke-RollPayAzureJson -Arguments (@('identity', 'federated-credential', 'list', '--identity-name', $IdentityName) + $targetArguments))
    foreach ($credential in $credentials) {
        if ($credential['name'] -cne $credentialName -or $credential['issuer'] -cne $issuer -or
            @($credential['audiences']).Count -ne 1 -or $credential['audiences'][0] -cne $audience) {
            throw '既存IDのOIDC信頼設定が想定と異なります。上書きせず停止します。'
        }
        if ($credential['subject'] -ceq $legacySubject) { $updateLegacySubject = $true }
        elseif ($credential['subject'] -cne $subject) {
            throw '既存IDのOIDC信頼設定が想定と異なります。上書きせず停止します。'
        }
    }
    if ($credentials.Count -gt 1) { throw 'OIDC信頼設定が重複しています。' }
}

if (-not $PSCmdlet.ShouldProcess("$IdentityName / $functionScope", "配置専用IDの準備、$subject のOIDC連携、Function App単体へのWebsite Contributor付与")) { return }

if (-not $identity) {
    $tagArguments = @($expectedTags.Keys | Sort-Object | ForEach-Object { "$_=$($expectedTags[$_])" })
    $identity = Invoke-RollPayAzureJson -Arguments (@('identity', 'create', '--name', $IdentityName, '--location', $function['location'], '--tags') + $tagArguments + $targetArguments)
}
if (-not $identity -or $identity['id'] -ne $identityScope -or -not $identity['clientId'] -or -not $identity['principalId']) {
    throw 'マネージドIDの作成結果にIDがありません。少し待って同じコマンドを再実行してください。'
}
$clientId = ([guid]$identity['clientId']).ToString()
$principalId = ([guid]$identity['principalId']).ToString()
if ($credentials.Count -eq 0) {
    Invoke-RollPayAzureJson -Arguments (@('identity', 'federated-credential', 'create', '--identity-name', $IdentityName,
        '--name', $credentialName, '--issuer', $issuer, '--subject', $subject, '--audiences', $audience) + $targetArguments) | Out-Null
}
elseif ($updateLegacySubject) {
    # 旧版の同一repo/mainだけを置き換え、旧Subjectを並行して信頼しない。
    Invoke-RollPayAzureJson -Arguments (@('identity', 'federated-credential', 'update', '--identity-name', $IdentityName,
        '--name', $credentialName, '--issuer', $issuer, '--subject', $subject, '--audiences', $audience) + $targetArguments) | Out-Null
}

$assignments = @(Invoke-RollPayAzureJson -Arguments @('role', 'assignment', 'list', '--scope', $functionScope, '--subscription', $subscription))
$existingRole = @($assignments | Where-Object {
    $_['principalId'] -eq $principalId -and $_['scope'] -eq $functionScope -and
    $_['roleDefinitionId'] -eq "/subscriptions/$subscription/providers/Microsoft.Authorization/roleDefinitions/$roleId"
})
if ($existingRole.Count -eq 0) {
    # Object IDと種類を明示し、作成直後のService PrincipalのGraph検索を避ける。
    Invoke-RollPayAzureJson -Arguments @('role', 'assignment', 'create', '--assignee-object-id', $principalId,
        '--assignee-principal-type', 'ServicePrincipal', '--role', $roleId, '--scope', $functionScope, '--subscription', $subscription) | Out-Null
}

$result = [pscustomobject]@{
    IdentityResourceId = $identityScope
    PrincipalId = $principalId
    FederatedSubject = $subject
    RoleScope = $functionScope
    RepositoryVariables = [ordered]@{
        ROLLPAY_BACKEND_DEPLOY_CLIENT_ID = $clientId
        ROLLPAY_AZURE_TENANT_ID = $TenantId.ToString()
        ROLLPAY_AZURE_SUBSCRIPTION_ID = $subscription
        ROLLPAY_FUNCTION_APP_NAME = [string]$function['name']
    }
}
Write-Host '配置専用IDを準備しました。RepositoryVariablesの4項目をGitHubのActions Variablesに登録してください。'
Write-RollPayLocalResult -Result $result -OutputPath $OutputPath
