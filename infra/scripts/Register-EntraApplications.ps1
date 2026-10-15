#Requires -Version 7.0
<#
.SYNOPSIS
Roll & Pay専用のSPA/APIアプリを登録し、委任スコープ・SPA事前承認を設定する。
.DESCRIPTION
既存テナントでaz loginを済ませてから実行する。秘密情報は作成しない。
同じ管理タグのアプリを再利用する。同名の管理外アプリや重複があれば停止する。
-WhatIfは読み取りと予定表示のみ。Azureリソースのデプロイは行わない。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][guid] $TenantId,
    [string] $SubscriptionId,
    [ValidateNotNullOrEmpty()][string] $ApiDisplayName = 'rollpay-api-prod',
    [ValidateNotNullOrEmpty()][string] $SpaDisplayName = 'rollpay-spa-prod',
    [string[]] $SpaRedirectUris = @('http://localhost:5173/'),
    [switch] $SetEnvironmentVariables,
    [ValidatePattern('\.local\.json$')][string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$script:RollPaySubscriptionId = $SubscriptionId
. (Join-Path $PSScriptRoot 'AzureCli.Common.ps1')
$managedTag = 'rollpay-managed:entra-bootstrap:v1'

if ($ApiDisplayName -eq $SpaDisplayName) { throw 'APIとSPAの名前は別にしてください。' }
foreach ($redirect in $SpaRedirectUris) {
    $uri = $null
    if (-not [uri]::TryCreate($redirect, [UriKind]::Absolute, [ref]$uri)) { throw "無効なリダイレクトURI: $redirect" }
    if ($uri.Fragment -or $uri.UserInfo -or $redirect.Contains('*')) { throw "リダイレクトURIにフラグメント・ユーザー情報・ワイルドカードは使えません: $redirect" }
    if ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.Host -eq 'localhost')) {
        throw "リダイレクトURIはHTTPSかlocalhostのHTTPのみです: $redirect"
    }
}

function Find-ManagedApplication {
    param([string] $DisplayName)
    $filter = [uri]::EscapeDataString("displayName eq '$($DisplayName.Replace("'", "''"))'")
    $apps = @(Get-RollPayGraphCollection -Url ('https://graph.microsoft.com/v1.0/applications?$filter=' + $filter))
    if ($apps.Count -gt 1) { throw "同名アプリが複数あります。自動選択しません: $DisplayName" }
    if ($apps.Count -eq 0) { return $null }
    $app = $apps[0]
    if ($managedTag -notin @($app['tags'])) { throw "管理外の同名アプリがあります。既存アプリを確認するか別名を指定してください: $DisplayName" }
    if ($app['signInAudience'] -ne 'AzureADMyOrg') { throw "既存アプリがSingle tenantではありません: $DisplayName" }
    return $app
}

function New-ManagedApplication {
    param([string] $DisplayName)
    return Invoke-RollPayGraph -Method POST -Url 'https://graph.microsoft.com/v1.0/applications' -Body @{
        displayName = $DisplayName
        signInAudience = 'AzureADMyOrg'
        tags = @($managedTag)
    }
}

function Ensure-ServicePrincipal {
    param([string] $ClientId)
    $filter = [uri]::EscapeDataString("appId eq '$ClientId'")
    $principals = @(Get-RollPayGraphCollection -Url ('https://graph.microsoft.com/v1.0/servicePrincipals?$filter=' + $filter))
    if ($principals.Count -gt 1) { throw '同じClient IDのService Principalが複数あります。' }
    if ($principals.Count -eq 0) {
        Invoke-RollPayGraph -Method POST -Url 'https://graph.microsoft.com/v1.0/servicePrincipals' -Body @{ appId = $ClientId } | Out-Null
    }
}

$context = Get-RollPayUserContext -ExpectedTenantId $TenantId
$api = Find-ManagedApplication -DisplayName $ApiDisplayName
$spa = Find-ManagedApplication -DisplayName $SpaDisplayName

if (-not $PSCmdlet.ShouldProcess("テナント $TenantId / $ApiDisplayName / $SpaDisplayName", '専用アプリの作成・スコープ設定・SPA事前承認・Service Principal作成')) { return }

if (-not $api) { $api = New-ManagedApplication -DisplayName $ApiDisplayName }
if (-not $spa) { $spa = New-ManagedApplication -DisplayName $SpaDisplayName }

$apiClientId = ([guid]$api['appId']).ToString()
$spaClientId = ([guid]$spa['appId']).ToString()
$apiObjectId = ([guid]$api['id']).ToString()
$spaObjectId = ([guid]$spa['id']).ToString()
$identifierUri = "api://$apiClientId"

$apiSettings = $api['api']
if (-not $apiSettings) { $apiSettings = @{} }
$scopes = @($apiSettings['oauth2PermissionScopes'] | Where-Object { $null -ne $_ })
$accessScopes = @($scopes | Where-Object { $_['value'] -eq 'access_as_user' })
if ($accessScopes.Count -gt 1) { throw 'access_as_userスコープが重複しています。' }
if ($accessScopes.Count -eq 0) {
    $scope = @{
        id = [guid]::NewGuid().ToString()
        value = 'access_as_user'
        type = 'Admin'
        isEnabled = $true
        adminConsentDisplayName = 'Use Roll & Pay as the signed-in user'
        adminConsentDescription = 'Allow the Roll & Pay SPA to call the API on behalf of the signed-in user.'
    }
    $scopes += $scope
}
else {
    $scope = $accessScopes[0]
    if ($scope['type'] -ne 'Admin' -or $scope['isEnabled'] -ne $true) {
        throw '既存のaccess_as_userが無効または種類不一致です。スコープIDを変更せず手動で確認してください。'
    }
}

# スコープは事前承認と同じPATCHで新設しない。
# Graphが事前承認を検証する時点で、参照先のスコープを公開済みにする。
Invoke-RollPayGraph -Method PATCH -Url "https://graph.microsoft.com/v1.0/applications/$apiObjectId" -Body @{
    identifierUris = @(@($api['identifierUris']) + $identifierUri | Where-Object { $_ } | Select-Object -Unique)
    api = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes = $scopes
    }
} | Out-Null

$scopePublished = $false
for ($attempt = 1; $attempt -le 6; $attempt++) {
    $published = Invoke-RollPayGraph -Url "https://graph.microsoft.com/v1.0/applications/$apiObjectId"
    $publishedScopes = @($published['api']['oauth2PermissionScopes'] | Where-Object {
        $_['id'] -eq $scope['id'] -and $_['value'] -eq 'access_as_user' -and $_['isEnabled'] -eq $true
    })
    if ($publishedScopes.Count -eq 1) {
        $scopePublished = $true
        $apiSettings = $published['api']
        break
    }
    if ($attempt -lt 6) {
        Write-Host "APIスコープの反映を待っています ($attempt/6)"
        Start-Sleep -Seconds 2
    }
}
if (-not $scopePublished) {
    throw 'APIスコープの反映を確認できませんでした。作成済みアプリは保持しています。少し待って同じコマンドを再実行してください。'
}

Ensure-ServicePrincipal -ClientId $apiClientId
Ensure-ServicePrincipal -ClientId $spaClientId

# 他クライアントの既存設定は保持し、今回のSPAの事前承認に必要スコープを追加する。
$preauthorized = @($apiSettings['preAuthorizedApplications'] | Where-Object { $null -ne $_ })
$spaAuthorization = @($preauthorized | Where-Object { $_['appId'] -eq $spaClientId })
if ($spaAuthorization.Count -gt 1) { throw 'SPAの事前承認設定が重複しています。' }
if ($spaAuthorization.Count -eq 0) {
    $preauthorized += @{ appId = $spaClientId; delegatedPermissionIds = @($scope['id']) }
}
else {
    $spaAuthorization[0]['delegatedPermissionIds'] = @(@($spaAuthorization[0]['delegatedPermissionIds']) + $scope['id'] | Select-Object -Unique)
}

$preauthorizationPatch = @{ api = @{ preAuthorizedApplications = $preauthorized } }
for ($attempt = 1; $attempt -le 6; $attempt++) {
    try {
        Invoke-RollPayGraph -Method PATCH -Url "https://graph.microsoft.com/v1.0/applications/$apiObjectId" -Body $preauthorizationPatch | Out-Null
        break
    }
    catch {
        # 読取りで見えても検証側への反映が遅れる場合だけ、同じ設定を再試行する。
        $scopeNotReplicated = $_.Exception.Message -match 'api\.preAuthorizedApplications\.delegatedPermissionIds' -and
            $_.Exception.Message -match 'cannot be found in the\s+AppPermissions'
        if (-not $scopeNotReplicated -or $attempt -eq 6) { throw }
        Write-Host "SPA事前承認に必要なスコープの反映を待っています ($attempt/6)"
        Start-Sleep -Seconds 2
    }
}

$requirements = @($spa['requiredResourceAccess'] | Where-Object { $null -ne $_ })
$apiRequirement = @($requirements | Where-Object { $_['resourceAppId'] -eq $apiClientId })
if ($apiRequirement.Count -gt 1) { throw 'SPAのAPI権限設定が重複しています。' }
if ($apiRequirement.Count -eq 0) {
    $requirements += @{ resourceAppId = $apiClientId; resourceAccess = @(@{ id = $scope['id']; type = 'Scope' }) }
}
else {
    $existingAccess = @($apiRequirement[0]['resourceAccess'])
    if (-not @($existingAccess | Where-Object { $_['id'] -eq $scope['id'] -and $_['type'] -eq 'Scope' }).Count) {
        $apiRequirement[0]['resourceAccess'] = @($existingAccess + @{ id = $scope['id']; type = 'Scope' })
    }
}
$spaSettings = $spa['spa']
if (-not $spaSettings) { $spaSettings = @{} }
$redirectUris = @(@($spaSettings['redirectUris']) + $SpaRedirectUris | Where-Object { $_ } | Select-Object -Unique)
Invoke-RollPayGraph -Method PATCH -Url "https://graph.microsoft.com/v1.0/applications/$spaObjectId" -Body @{
    spa = @{ redirectUris = $redirectUris }
    requiredResourceAccess = $requirements
} | Out-Null

$result = [pscustomobject]@{
    TenantId = $context.TenantId
    SubscriptionId = $context.SubscriptionId
    ApiClientId = $apiClientId
    SpaClientId = $spaClientId
    AdministratorObjectId = $context.AdministratorObjectId
    ApiScope = "$identifierUri/access_as_user"
    ApiApplicationObjectId = $apiObjectId
    SpaApplicationObjectId = $spaObjectId
    SpaRedirectUris = $redirectUris
}
if ($SetEnvironmentVariables) {
    $env:ROLLPAY_API_CLIENT_ID = $apiClientId
    $env:ROLLPAY_SPA_CLIENT_ID = $spaClientId
    $env:ROLLPAY_ADMIN_OBJECT_ID = $context.AdministratorObjectId
}
Write-RollPayLocalResult -Result $result -OutputPath $OutputPath
