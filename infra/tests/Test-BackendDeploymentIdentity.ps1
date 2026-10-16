#Requires -Version 7.0
# Azure CLIをモックに置き換える。実Azureへのアクセスは行わない。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$register = Join-Path $PSScriptRoot '../scripts/Register-BackendDeploymentIdentity.ps1'
$tenantId = '11111111-1111-1111-1111-111111111111'
$subscriptionId = '22222222-2222-2222-2222-222222222222'
$functionScope = "/subscriptions/$subscriptionId/resourceGroups/rg-rollpay-prod-jpe/providers/Microsoft.Web/sites/func-rollpay-test"
$options = @{ TenantId = $tenantId; SubscriptionId = $subscriptionId; FunctionAppName = 'func-rollpay-test' }
$outputPath = Join-Path ([IO.Path]::GetTempPath()) ("rollpay-deploy-test-$([guid]::NewGuid()).local.json")
$oldAz = Get-Item Function:global:az -ErrorAction SilentlyContinue
$global:RollPayDeploymentTestState = @{
    TenantId = $tenantId
    SubscriptionId = $subscriptionId
    FunctionScope = $functionScope
    Kind = 'functionapp,linux'
    Identities = [Collections.Generic.List[hashtable]]::new()
    Credentials = [Collections.Generic.List[hashtable]]::new()
    Assignments = [Collections.Generic.List[hashtable]]::new()
    Writes = 0
    FailRoleCreate = $false
}

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Assert-Throws {
    param([scriptblock] $Action, [string] $Pattern)
    $caught = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $caught = $true
    }
    Assert-Condition $caught "Expected rejection: $Pattern"
}

function global:az {
    $cliArguments = @($args)
    $global:LASTEXITCODE = 0
    $state = $global:RollPayDeploymentTestState
    function Get-Argument {
        param([string] $Name)
        $position = [array]::IndexOf($cliArguments, $Name)
        if ($position -lt 0) { throw "Missing expected argument: $Name" }
        return $cliArguments[$position + 1]
    }
    if (($cliArguments[0..1] -join ' ') -eq 'account show') {
        return @{ id = $state.SubscriptionId; tenantId = $state.TenantId; environmentName = 'AzureCloud' } | ConvertTo-Json
    }
    Assert-Condition ((Get-Argument '--subscription') -eq '22222222-2222-2222-2222-222222222222') 'Explicit subscription on every resource command'
    if (($cliArguments[0..1] -join ' ') -eq 'functionapp show') {
        return @{ id = $state.FunctionScope; name = 'func-rollpay-test'; kind = $state.Kind; location = 'japaneast' } | ConvertTo-Json
    }
    if (($cliArguments[0..1] -join ' ') -eq 'identity list') {
        return ConvertTo-Json -InputObject @($state.Identities) -Depth 10
    }
    if (($cliArguments[0..1] -join ' ') -eq 'identity create') {
        $state.Writes++
        $tags = @{}
        $start = [array]::IndexOf($cliArguments, '--tags') + 1
        for ($index = $start; $index -lt $cliArguments.Count -and -not $cliArguments[$index].StartsWith('--'); $index++) {
            $pair = $cliArguments[$index].Split('=', 2)
            $tags[$pair[0]] = $pair[1]
        }
        $identity = @{
            name = Get-Argument '--name'
            id = '/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-rollpay-prod-jpe/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-rollpay-backend-deploy-prod'
            tenantId = $state.TenantId
            clientId = '33333333-3333-3333-3333-333333333333'
            principalId = '44444444-4444-4444-4444-444444444444'
            tags = $tags
        }
        $state.Identities.Add($identity)
        return $identity | ConvertTo-Json -Depth 10
    }
    if (($cliArguments[0..2] -join ' ') -eq 'identity federated-credential list') {
        return ConvertTo-Json -InputObject @($state.Credentials) -Depth 10
    }
    if (($cliArguments[0..2] -join ' ') -eq 'identity federated-credential create') {
        $state.Writes++
        $credential = @{
            name = Get-Argument '--name'
            issuer = Get-Argument '--issuer'
            subject = Get-Argument '--subject'
            audiences = @((Get-Argument '--audiences'))
        }
        $state.Credentials.Add($credential)
        return $credential | ConvertTo-Json -Depth 10
    }
    if (($cliArguments[0..2] -join ' ') -eq 'identity federated-credential update') {
        Assert-Condition ((Get-Argument '--name') -eq 'github-main') 'Only migrate the managed main credential'
        Assert-Condition ((Get-Argument '--issuer') -ceq $state.Credentials[0].issuer) 'Preserve issuer and supply it for Azure CLI 2.61'
        Assert-Condition ((Get-Argument '--audiences') -ceq $state.Credentials[0].audiences[0]) 'Preserve audience during migration'
        $state.Writes++
        $state.Credentials[0].subject = Get-Argument '--subject'
        return $state.Credentials[0] | ConvertTo-Json -Depth 10
    }
    if (($cliArguments[0..2] -join ' ') -eq 'role assignment list') {
        Assert-Condition ((Get-Argument '--scope') -eq $state.FunctionScope) 'Read roles at the Function App scope'
        return ConvertTo-Json -InputObject @($state.Assignments) -Depth 10
    }
    if (($cliArguments[0..2] -join ' ') -eq 'role assignment create') {
        if ($state.FailRoleCreate) { $global:LASTEXITCODE = 1; return 'AuthorizationFailed: roleAssignments/write' }
        Assert-Condition ((Get-Argument '--scope') -eq $state.FunctionScope) 'Never grant at resource group or subscription scope'
        Assert-Condition ((Get-Argument '--role') -eq 'de139f84-1756-47ae-9be6-808fbbe84772') 'Only Website Contributor'
        Assert-Condition ((Get-Argument '--assignee-object-id') -eq $state.Identities[0].principalId) 'Use principal Object ID, not Client ID'
        Assert-Condition ((Get-Argument '--assignee-principal-type') -eq 'ServicePrincipal') 'Avoid principal replication lookup'
        $state.Writes++
        $assignment = @{
            principalId = Get-Argument '--assignee-object-id'
            scope = Get-Argument '--scope'
            roleDefinitionId = "/subscriptions/$($state.SubscriptionId)/providers/Microsoft.Authorization/roleDefinitions/$(Get-Argument '--role')"
        }
        $state.Assignments.Add($assignment)
        return $assignment | ConvertTo-Json
    }
    throw "Unexpected Azure CLI command: $($cliArguments -join ' ')"
}

try {
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($register, [ref]$null, [ref]$parseErrors) | Out-Null
    Assert-Condition (-not $parseErrors) 'PowerShell syntax'
    $state = $global:RollPayDeploymentTestState

    & $register @options -WhatIf -OutputPath $outputPath
    Assert-Condition ($state.Writes -eq 0 -and -not (Test-Path -LiteralPath $outputPath)) 'WhatIf must not write Azure or a local result'

    # 途中のRBAC権限エラーを再現。成功結果を出さず、次回に再開する。
    $state.FailRoleCreate = $true
    Assert-Throws { & $register @options -OutputPath $outputPath } 'AuthorizationFailed'
    Assert-Condition ($state.Writes -eq 2 -and -not (Test-Path -LiteralPath $outputPath)) 'Partial failure retains identity and trust but emits no success file'
    $state.FailRoleCreate = $false
    $first = & $register @options -OutputPath $outputPath
    Assert-Condition ($state.Writes -eq 3) 'Resume only the missing role assignment'
    Assert-Condition ($state.Identities.Count -eq 1 -and $state.Credentials.Count -eq 1 -and $state.Assignments.Count -eq 1) 'Exactly one identity, trust, and role'
    Assert-Condition ($state.Credentials[0].subject -ceq 'repo:ChizenKomamiya@33175412/roll-and-pay@1418381637:ref:refs/heads/main') 'Trust only the immutable owner/repository IDs and main branch'
    Assert-Condition ($state.Credentials[0].issuer -ceq 'https://token.actions.githubusercontent.com') 'GitHub OIDC issuer'
    Assert-Condition ($state.Credentials[0].audiences[0] -ceq 'api://AzureADTokenExchange') 'Azure token exchange audience'
    Assert-Condition ($first.RepositoryVariables.ROLLPAY_BACKEND_DEPLOY_CLIENT_ID -eq $state.Identities[0].clientId) 'Return Client ID for GitHub'
    Assert-Condition ($first.RepositoryVariables.ROLLPAY_AZURE_TENANT_ID -eq $tenantId -and $first.RepositoryVariables.ROLLPAY_AZURE_SUBSCRIPTION_ID -eq $subscriptionId) 'Return target tenant and subscription'
    Assert-Condition ($first.RepositoryVariables.ROLLPAY_FUNCTION_APP_NAME -eq 'func-rollpay-test' -and $first.RepositoryVariables.Count -eq 4) 'Return exactly four workflow variables'
    $saved = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    Assert-Condition ($saved.RepositoryVariables.ROLLPAY_BACKEND_DEPLOY_CLIENT_ID -eq $first.RepositoryVariables.ROLLPAY_BACKEND_DEPLOY_CLIENT_ID) 'Persist the returned GitHub variables'

    $second = & $register @options
    Assert-Condition ($state.Writes -eq 3 -and $first.IdentityResourceId -eq $second.IdentityResourceId) 'Rerun preserves identity and performs no writes'
    & $register @options -WhatIf
    Assert-Condition ($state.Writes -eq 3) 'WhatIf with existing resources is read-only'

    $state.TenantId = '55555555-5555-5555-5555-555555555555'
    Assert-Throws { & $register @options } 'テナント'
    $state.TenantId = $tenantId
    $state.SubscriptionId = '55555555-5555-5555-5555-555555555555'
    Assert-Throws { & $register @options } 'サブスクリプション'
    $state.SubscriptionId = $subscriptionId
    $state.Kind = 'app,linux'
    Assert-Throws { & $register @options } 'Function App'
    $state.Kind = 'functionapp,linux'
    $state.FunctionScope = $functionScope.Replace('func-rollpay-test', 'different-app')
    Assert-Throws { & $register @options } 'Function App'
    $state.FunctionScope = $functionScope

    $identity = $state.Identities[0]
    $tags = $identity.tags
    $identity.tags = @{}
    Assert-Throws { & $register @options } '管理外'
    $identity.tags = $tags
    $identity.tags['rollpay-function-app'] = 'different-app'
    Assert-Throws { & $register @options } '配置先が異なる'
    $identity.tags['rollpay-function-app'] = 'func-rollpay-test'
    $state.Identities.Add($identity.Clone())
    Assert-Throws { & $register @options } '複数'
    $state.Identities.RemoveAt(1)
    $credential = $state.Credentials[0]
    $originalSubject = $credential.subject
    $credential.subject = 'repo:ChizenKomamiya/roll-and-pay:pull_request'
    Assert-Throws { & $register @options } '信頼設定'
    $credential.subject = $originalSubject.ToLowerInvariant()
    Assert-Throws { & $register @options } '信頼設定'
    $credential.subject = $originalSubject.Replace('@33175412/', '@99999999/')
    Assert-Throws { & $register @options } '信頼設定'
    $credential.subject = $originalSubject.Replace('@1418381637:', '@99999999:')
    Assert-Throws { & $register @options } '信頼設定'
    $credential.subject = $originalSubject
    $credential.audiences = @('api://AzureADTokenExchange', 'unexpected')
    Assert-Throws { & $register @options } '信頼設定'
    $credential.audiences = @('api://AzureADTokenExchange')
    $state.Credentials.Add(@{ name = 'other-credential' })
    Assert-Throws { & $register @options } '信頼設定'
    $state.Credentials.RemoveAt(1)
    Assert-Condition ($state.Writes -eq 3) 'Invalid contexts, collisions and trust mismatches must not mutate Azure'

    # 旧版が作った同一repo/mainの信頼だけを、その場で新形式へ移行する。
    $credential.subject = 'repo:ChizenKomamiya/roll-and-pay:ref:refs/heads/main'
    $credential.issuer = 'https://unexpected.example/'
    Assert-Throws { & $register @options } '信頼設定'
    $credential.issuer = 'https://token.actions.githubusercontent.com'
    & $register @options -WhatIf
    Assert-Condition ($state.Writes -eq 3 -and $credential.subject -cne $originalSubject) 'WhatIf must not migrate legacy trust'
    $migrated = & $register @options
    Assert-Condition ($state.Writes -eq 4 -and $credential.subject -ceq $originalSubject) 'Migrate only the subject'
    Assert-Condition ($state.Credentials.Count -eq 1 -and $state.Assignments.Count -eq 1 -and $state.Identities.Count -eq 1) 'Migration preserves identity and grants without leaving legacy trust'
    Assert-Condition ($migrated.RepositoryVariables.ROLLPAY_BACKEND_DEPLOY_CLIENT_ID -eq $first.RepositoryVariables.ROLLPAY_BACKEND_DEPLOY_CLIENT_ID) 'Migration keeps the GitHub Client ID'
    & $register @options | Out-Null
    Assert-Condition ($state.Writes -eq 4) 'Rerun after migration performs no writes'
    Write-Host 'PASS: Backend deployment identity (immutable subject, legacy migration, WhatIf, partial recovery, least scope, rerun, output and rejection cases).'
}
finally {
    if ($oldAz) { Set-Item Function:global:az -Value $oldAz.ScriptBlock }
    else { Remove-Item Function:global:az }
    Remove-Variable RollPayDeploymentTestState -Scope Global
    if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Force }
}
