#Requires -Version 7.0
# オフライン検証。azを関数で置き換え、実Azureには一切アクセスしない。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scripts = Join-Path $PSScriptRoot '../scripts'
$tenantId = '11111111-1111-1111-1111-111111111111'
$userId = '22222222-2222-2222-2222-222222222222'
$subscriptionId = '33333333-3333-3333-3333-333333333333'
$global:RollPayTestState = @{
    TenantId = $tenantId
    Applications = [Collections.Generic.List[hashtable]]::new()
    Principals = [Collections.Generic.List[hashtable]]::new()
    Writes = 0
    PendingScopeReads = 0
    PreauthorizationFailures = 1
    ScopeReadRetries = 0
    PreauthorizationRetries = 0
}
$oldAz = Get-Item Function:global:az -ErrorAction SilentlyContinue

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function Assert-Throws {
    param([scriptblock] $Action, [string] $MessagePattern)
    $caught = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) { throw }
        $caught = $true
    }
    Assert-Condition $caught "Expected rejection: $MessagePattern"
}

function global:az {
    $cliArguments = @($args)
    $global:LASTEXITCODE = 0
    $state = $global:RollPayTestState
    # signed-in-userは--subscriptionを受け付けない。実CLIの引数エラーを再現する。
    if ($cliArguments[0] -eq 'ad' -and '--subscription' -in $cliArguments) {
        $global:LASTEXITCODE = 2
        return 'ERROR: unrecognized arguments: --subscription'
    }
    if ($cliArguments[0] -eq 'account') {
        return @{ tenantId = $state.TenantId; id = '33333333-3333-3333-3333-333333333333'; environmentName = 'AzureCloud' } | ConvertTo-Json
    }
    if ($cliArguments[0] -eq 'ad') {
        return @{ id = '22222222-2222-2222-2222-222222222222' } | ConvertTo-Json
    }
    if ($cliArguments[0] -ne 'rest') { throw 'Unexpected CLI command in offline test' }
    $method = $cliArguments[([array]::IndexOf($cliArguments, '--method') + 1)]
    $uri = [uri]$cliArguments[([array]::IndexOf($cliArguments, '--url') + 1)]
    $body = $null
    if ('--body' -in $cliArguments) {
        $bodyArgument = $cliArguments[([array]::IndexOf($cliArguments, '--body') + 1)]
        $body = Get-Content -LiteralPath $bodyArgument.Substring(1) -Raw | ConvertFrom-Json -AsHashtable
    }
    $filter = [uri]::UnescapeDataString($uri.Query)
    if ($method -eq 'GET' -and $uri.AbsolutePath -eq '/v1.0/applications') {
        $match = [regex]::Match($filter, "displayName eq '(.*)'")
        $name = $match.Groups[1].Value.Replace("''", "'")
        return @{ value = @($state.Applications | Where-Object { $_['displayName'] -eq $name }) } | ConvertTo-Json -Depth 30
    }
    if ($method -eq 'GET' -and $uri.AbsolutePath.StartsWith('/v1.0/applications/')) {
        $app = @($state.Applications | Where-Object { $_['id'] -eq $uri.Segments[-1] })[0]
        $snapshot = $app | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
        if ($state.PendingScopeReads -gt 0) {
            $state.PendingScopeReads--
            $state.ScopeReadRetries++
            $snapshot.api.oauth2PermissionScopes = @()
        }
        return $snapshot | ConvertTo-Json -Depth 30
    }
    if ($method -eq 'POST' -and $uri.AbsolutePath -eq '/v1.0/applications') {
        $state.Writes++
        $app = @{
            id = [guid]::NewGuid().ToString()
            appId = [guid]::NewGuid().ToString()
            displayName = $body.displayName
            signInAudience = $body.signInAudience
            tags = $body.tags
            identifierUris = @()
            api = @{ oauth2PermissionScopes = @(); preAuthorizedApplications = @() }
            spa = @{ redirectUris = @() }
            requiredResourceAccess = @()
        }
        $state.Applications.Add($app)
        return $app | ConvertTo-Json -Depth 30
    }
    if ($method -eq 'PATCH' -and $uri.AbsolutePath.StartsWith('/v1.0/applications/')) {
        $id = $uri.Segments[-1]
        $app = @($state.Applications | Where-Object { $_['id'] -eq $id })[0]
        if ($body.ContainsKey('api') -and $body.api.ContainsKey('preAuthorizedApplications')) {
            $publishedIds = @($app.api.oauth2PermissionScopes | ForEach-Object { $_['id'] })
            foreach ($authorization in $body.api.preAuthorizedApplications) {
                foreach ($permissionId in $authorization.delegatedPermissionIds) {
                    if ($permissionId -notin $publishedIds) {
                        $global:LASTEXITCODE = 1
                        return 'api.preAuthorizedApplications.delegatedPermissionIds has a Permission Id that cannot be found in the AppPermissions sets.'
                    }
                }
            }
            if ($state.PreauthorizationFailures -gt 0) {
                $state.PreauthorizationFailures--
                $state.PreauthorizationRetries++
                $global:LASTEXITCODE = 1
                return 'api.preAuthorizedApplications.delegatedPermissionIds has a Permission Id that cannot be found in the AppPermissions sets.'
            }
        }
        $state.Writes++
        foreach ($key in $body.Keys) {
            if ($key -eq 'api') {
                if ($body.api.ContainsKey('oauth2PermissionScopes') -and $app.api.oauth2PermissionScopes.Count -eq 0) {
                    $state.PendingScopeReads = 1
                }
                foreach ($apiKey in $body.api.Keys) { $app.api[$apiKey] = $body.api[$apiKey] }
            }
            else { $app[$key] = $body[$key] }
        }
        return
    }
    if ($method -eq 'GET' -and $uri.AbsolutePath -eq '/v1.0/servicePrincipals') {
        $clientId = [regex]::Match($filter, "appId eq '(.*)'").Groups[1].Value
        return @{ value = @($state.Principals | Where-Object { $_['appId'] -eq $clientId }) } | ConvertTo-Json -Depth 30
    }
    if ($method -eq 'POST' -and $uri.AbsolutePath -eq '/v1.0/servicePrincipals') {
        $state.Writes++
        $principal = @{ id = [guid]::NewGuid().ToString(); appId = $body.appId }
        $state.Principals.Add($principal)
        return $principal | ConvertTo-Json
    }
    throw "Unexpected Graph request: $method $($uri.AbsolutePath)"
}

try {
    Get-ChildItem -LiteralPath $scripts -Filter '*.ps1' | ForEach-Object {
        $tokens = $null
        $errors = $null
        [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
        Assert-Condition (-not $errors) "PowerShell parsing: $($_.Name)"
    }

    $register = Join-Path $scripts 'Register-EntraApplications.ps1'
    $getUser = Join-Path $scripts 'Get-AdministratorObjectId.ps1'
    & $register -TenantId $tenantId -SubscriptionId $subscriptionId -WhatIf
    Assert-Condition ($global:RollPayTestState.Writes -eq 0) 'WhatIf must not mutate Graph'

    $first = & $register -TenantId $tenantId -SubscriptionId $subscriptionId
    $api = @($global:RollPayTestState.Applications | Where-Object { $_['displayName'] -eq 'rollpay-api-prod' })[0]
    $spa = @($global:RollPayTestState.Applications | Where-Object { $_['displayName'] -eq 'rollpay-spa-prod' })[0]
    $scopeId = $api.api.oauth2PermissionScopes[0].id
    Assert-Condition ($first.AdministratorObjectId -eq $userId) 'Target tenant user Object ID'
    Assert-Condition ($api.api.requestedAccessTokenVersion -eq 2) 'v2 API token'
    Assert-Condition ($api.api.preAuthorizedApplications[0].appId -eq $first.SpaClientId) 'SPA preauthorization'
    Assert-Condition ($spa.requiredResourceAccess[0].resourceAccess[0].id -eq $scopeId) 'Delegated API scope'
    Assert-Condition ($global:RollPayTestState.ScopeReadRetries -eq 1) 'Wait for scope read propagation'
    Assert-Condition ($global:RollPayTestState.PreauthorizationRetries -eq 1) 'Retry only preauthorization replication failure'

    $second = & $register -TenantId $tenantId -SubscriptionId $subscriptionId -SpaRedirectUris @('https://test.azurestaticapps.net/')
    Assert-Condition ($global:RollPayTestState.Applications.Count -eq 2) 'No duplicate apps on rerun'
    Assert-Condition ($global:RollPayTestState.Principals.Count -eq 2) 'No duplicate service principals'
    Assert-Condition ($second.ApiClientId -eq $first.ApiClientId -and $second.SpaClientId -eq $first.SpaClientId) 'Client IDs remain stable'
    Assert-Condition ($api.api.oauth2PermissionScopes.Count -eq 1 -and $api.api.oauth2PermissionScopes[0].id -eq $scopeId) 'Scope ID remains stable'
    Assert-Condition ($api.api.preAuthorizedApplications[0].delegatedPermissionIds.Count -eq 1) 'No duplicate preauthorization scopes'
    Assert-Condition ($spa.requiredResourceAccess[0].resourceAccess.Count -eq 1) 'No duplicate required permissions'
    Assert-Condition ($spa.spa.redirectUris.Count -eq 2) 'Add SWA URI without deleting localhost'

    $writesBefore = $global:RollPayTestState.Writes
    $identity = & $getUser -TenantId $tenantId -SubscriptionId $subscriptionId
    Assert-Condition ($identity.AdministratorObjectId -eq $userId -and $global:RollPayTestState.Writes -eq $writesBefore) 'Object ID script is read-only'

    Assert-Throws { & $register -TenantId '44444444-4444-4444-4444-444444444444' } 'テナント'
    Assert-Throws { & $getUser -TenantId $tenantId -SubscriptionId '55555555-5555-5555-5555-555555555555' } 'サブスクリプション'
    Assert-Throws { & $register -TenantId $tenantId -SpaRedirectUris @('http://example.com/') } 'HTTPS'
    Assert-Throws { & $register -TenantId $tenantId -SpaRedirectUris @('https://example.com/#fragment') } 'フラグメント'
    $api.tags = @()
    Assert-Throws { & $register -TenantId $tenantId } '管理外'
    $api.tags = @('rollpay-managed:entra-bootstrap:v1')
    $global:RollPayTestState.Applications.Add($api.Clone())
    Assert-Throws { & $register -TenantId $tenantId } '複数'
    Assert-Condition ($global:RollPayTestState.Writes -eq $writesBefore) 'Rejected requests must not mutate Graph'
    Write-Host 'PASS: Entra scripts offline validation (initial registration, rerun, URI addition, WhatIf, identity, rejection cases).'
}
finally {
    if ($oldAz) { Set-Item Function:global:az -Value $oldAz.ScriptBlock }
    else { Remove-Item Function:global:az }
    Remove-Variable RollPayTestState -Scope Global
}
