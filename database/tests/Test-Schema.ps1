#Requires -Version 7.0
# Offline tests. The Azure CLI is replaced; no Azure requests or resource changes occur.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$directory = Split-Path $PSScriptRoot -Parent
$applyScript = Join-Path $directory 'Apply-Schema.ps1'
$definition = Get-Content (Join-Path $directory 'container-definition.json') -Raw | ConvertFrom-Json -AsHashtable
$subscription = '11111111-1111-1111-1111-111111111111'
$parameters = @{ SubscriptionId = $subscription; ResourceGroupName = 'rg-rollpay-test'; AccountName = 'cosmos-rollpay-test' }
$oldAz = Get-Item Function:global:az -ErrorAction SilentlyContinue
$global:DatabaseSchemaTest = @{
    Subscription = $subscription
    Kind = 'GlobalDocumentDB'
    Capabilities = @(@{ name = 'EnableServerless' })
    Consistency = 'Session'
    DatabaseExists = $false
    ContainerExists = $false
    Container = $null
    Desired = $definition
    Writes = 0
    Previews = 0
    Fail = $false
    EmitDiagnostic = $false
    InvalidPreviewJson = $false
}

function Assert-Condition([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Assert-Rejected([scriptblock] $Action, [string] $Pattern) {
    $before = $global:DatabaseSchemaTest.Writes
    $caught = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $caught = $true
    }
    Assert-Condition $caught "Expected rejection: $Pattern"
    Assert-Condition ($global:DatabaseSchemaTest.Writes -eq $before) 'Rejected request must not write'
}

function global:az {
    $arguments = @($args)
    $state = $global:DatabaseSchemaTest
    if ('--subscription' -notin $arguments -or
        $arguments[([array]::IndexOf($arguments, '--subscription') + 1)] -ne $state.Subscription) {
        throw 'Every request must explicitly select the subscription'
    }
    $global:LASTEXITCODE = 0
    if ($state.EmitDiagnostic) {
        Write-Error 'Simulated CLI diagnostic on stderr.' -ErrorAction Continue
    }
    if ($state.Fail) {
        $global:LASTEXITCODE = 1
        return 'simulated CLI failure'
    }
    $prefix = $arguments[0..1] -join ' '
    if ($prefix -eq 'account show') {
        return @{ id = $state.Subscription; environmentName = 'AzureCloud' } | ConvertTo-Json
    }
    if ($prefix -eq 'cosmosdb show') {
        return @{
            kind = $state.Kind; capabilities = $state.Capabilities
            consistencyPolicy = @{ defaultConsistencyLevel = $state.Consistency }
        } | ConvertTo-Json -Depth 10
    }
    if ($prefix -eq 'cosmosdb sql') {
        $operation = $arguments[2..3] -join ' '
        switch ($operation) {
            'database exists' { return $state.DatabaseExists | ConvertTo-Json }
            'container exists' { return $state.ContainerExists | ConvertTo-Json }
            'container show' { return $state.Container | ConvertTo-Json -Depth 30 }
            default { throw "Unexpected SQL command: $operation" }
        }
    }
    if ($prefix -eq 'deployment group') {
        if ($arguments[([array]::IndexOf($arguments, '--mode') + 1)] -ne 'Incremental') {
            throw 'Deployment must not use Complete mode'
        }
        if ($arguments[2] -eq 'what-if') {
            $state.Previews++
            # Match CLI rendering behavior: --output json alone does not disable pretty output.
            if ('--no-pretty-print' -notin $arguments) {
                return 'Note: The result may contain false positive predictions. Resource changes: no change.'
            }
            if ($state.InvalidPreviewJson) { return 'Unexpected non-JSON output' }
            return @{ changes = @() } | ConvertTo-Json
        }
        if ($arguments[2] -eq 'create') {
            $state.Writes++
            $state.DatabaseExists = $true
            $state.ContainerExists = $true
            $state.Container = @{ resource = @{
                partitionKey = ($state.Desired.partitionKey | ConvertTo-Json | ConvertFrom-Json -AsHashtable)
                indexingPolicy = $state.Desired.indexingPolicy
            } }
            return @{ properties = @{ provisioningState = 'Succeeded' } } | ConvertTo-Json
        }
    }
    throw "Unexpected CLI command: $($arguments -join ' ')"
}

try {
    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile($applyScript, [ref]$tokens, [ref]$parseErrors) | Out-Null
    Assert-Condition (-not $parseErrors) 'PowerShell syntax'

    & $applyScript @parameters | Out-Null
    Assert-Condition ($global:DatabaseSchemaTest.Previews -eq 1 -and $global:DatabaseSchemaTest.Writes -eq 0) 'Default is preview only'
    $global:DatabaseSchemaTest.EmitDiagnostic = $true
    $preview = & $applyScript @parameters -WarningAction SilentlyContinue
    Assert-Condition ($preview.ContainsKey('changes') -and $global:DatabaseSchemaTest.Writes -eq 0) 'stderr must not corrupt preview JSON'
    $global:DatabaseSchemaTest.EmitDiagnostic = $false
    $global:DatabaseSchemaTest.InvalidPreviewJson = $true
    Assert-Rejected { & $applyScript @parameters } 'Azure CLI returned invalid JSON'
    $global:DatabaseSchemaTest.InvalidPreviewJson = $false
    & $applyScript @parameters -Apply -WhatIf | Out-Null
    Assert-Condition ($global:DatabaseSchemaTest.Writes -eq 0) 'WhatIf must not deploy'

    $first = & $applyScript @parameters -Apply
    Assert-Condition ($first.DatabaseName -eq 'rollpay' -and $first.ContainerName -eq 'app') 'Create the designed resources'
    $second = & $applyScript @parameters -Apply
    Assert-Condition ($second.Status -eq 'Succeeded' -and $global:DatabaseSchemaTest.Writes -eq 2) 'Rerun applies the same identities'

    $global:DatabaseSchemaTest.ContainerExists = $false
    & $applyScript @parameters | Out-Null
    Assert-Condition ($global:DatabaseSchemaTest.Writes -eq 2) 'Existing database with missing container can be previewed'
    $global:DatabaseSchemaTest.ContainerExists = $true

    $resource = $global:DatabaseSchemaTest.Container.resource
    $resource.partitionKey.paths = @('/wrong')
    Assert-Rejected { & $applyScript @parameters -Apply } 'Partition key'
    $resource.partitionKey.paths = @('/ownerKey')
    foreach ($ttl in @(60, -1)) {
        $resource.defaultTtl = $ttl
        Assert-Rejected { & $applyScript @parameters -Apply } 'TTL'
    }
    $resource.Remove('defaultTtl')
    $resource.uniqueKeyPolicy = @{ uniqueKeys = @(@{ paths = @('/email') }) }
    Assert-Rejected { & $applyScript @parameters -Apply } 'Unique Key'
    $resource.Remove('uniqueKeyPolicy')

    $global:DatabaseSchemaTest.Kind = 'MongoDB'
    Assert-Rejected { & $applyScript @parameters -Apply } 'NoSQL Serverless'
    $global:DatabaseSchemaTest.Kind = 'GlobalDocumentDB'
    $global:DatabaseSchemaTest.Capabilities = @()
    Assert-Rejected { & $applyScript @parameters -Apply } 'NoSQL Serverless'
    $global:DatabaseSchemaTest.Capabilities = @(@{ name = 'EnableServerless' })
    $global:DatabaseSchemaTest.Consistency = 'Eventual'
    Assert-Rejected { & $applyScript @parameters -Apply } 'Session'
    $global:DatabaseSchemaTest.Consistency = 'Session'
    $global:DatabaseSchemaTest.Fail = $true
    Assert-Rejected { & $applyScript @parameters -Apply } 'Azure CLI failed'

    Write-Host 'PASS: offline schema tests (JSON preview, stderr separation, invalid JSON, WhatIf, initial creation, rerun, incompatible resources, CLI failure).'
}
finally {
    if ($oldAz) { Set-Item Function:global:az -Value $oldAz.ScriptBlock }
    else { Remove-Item Function:global:az }
    Remove-Variable DatabaseSchemaTest -Scope Global
}
