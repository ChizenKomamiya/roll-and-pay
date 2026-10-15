#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][guid] $SubscriptionId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $ResourceGroupName,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{1,42}[a-z0-9]$')][string] $AccountName,
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DatabaseAzureJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    # Temporary diagnostic capture is needed even during -Apply -WhatIf.
    # Resource writes are gated by the caller's ShouldProcess, outside this helper.
    $WhatIfPreference = $false
    $command = Get-Command az -ErrorAction Stop
    $cliArguments = @($Arguments) + @(
        '--subscription', $SubscriptionId.ToString(),
        '--only-show-errors', '--output', 'json'
    )
    # Keep CLI diagnostics out of the JSON on stdout, including successful commands.
    $stderrPath = Join-Path ([IO.Path]::GetTempPath()) ('rollpay-schema-' + [guid]::NewGuid().ToString() + '.stderr')
    try {
        $global:LASTEXITCODE = 0
        $result = @(& $command @cliArguments 2> $stderrPath)
        $exitCode = $LASTEXITCODE
        $diagnostic = if (Test-Path -LiteralPath $stderrPath) {
            (Get-Content -LiteralPath $stderrPath -Raw -ErrorAction Stop)
        }
        if ($exitCode -ne 0) {
            throw "Azure CLI failed: $($Arguments[0..1] -join ' '). Check login, resource names and management-plane permissions.`n$diagnostic`n$($result -join "`n")"
        }
        if ($diagnostic) { Write-Warning $diagnostic.Trim() }
        $json = ($result -join "`n").Trim()
        if (-not $json) { throw 'Azure CLI returned no JSON result.' }
        try {
            return ConvertFrom-Json -InputObject $json -AsHashtable -ErrorAction Stop
        }
        catch {
            throw "Azure CLI returned invalid JSON for '$($Arguments -join ' ')'. $($_.Exception.Message)"
        }
    }
    finally {
        if (Test-Path -LiteralPath $stderrPath) {
            Remove-Item -LiteralPath $stderrPath -Force -WhatIf:$false -Confirm:$false
        }
    }
}

function Assert-CompatibleContainer {
    param([Parameter(Mandatory)][System.Collections.IDictionary] $Container)

    $resource = $Container['resource']
    if (-not $resource) { throw 'Container resource could not be read.' }
    $partition = $resource['partitionKey']
    if (-not $partition -or @($partition['paths']).Count -ne 1 -or
        $partition['paths'][0] -cne $definition['partitionKey']['paths'][0] -or
        $partition['kind'] -cne $definition['partitionKey']['kind'] -or
        $partition['version'] -ne $definition['partitionKey']['version']) {
        throw 'Partition key differs from the design. Stop; no automatic deletion or recreation is supported.'
    }
    if ($null -ne $resource['defaultTtl']) {
        throw 'TTL is enabled (including -1). Stop; the design requires TTL disabled, not item-level expiration.'
    }
    $uniquePolicy = $resource['uniqueKeyPolicy']
    if ($uniquePolicy -and @($uniquePolicy['uniqueKeys']).Count -gt 0) {
        throw 'Unique Key policy differs from the design. Stop; no automatic recreation is supported.'
    }
}

$definition = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'container-definition.json') -Raw |
    ConvertFrom-Json -AsHashtable
$templatePath = Join-Path $PSScriptRoot 'main.bicep'
$target = "$SubscriptionId/$ResourceGroupName/$AccountName/$($definition['databaseName'])/$($definition['containerName'])"

# Every request is explicitly subscription-scoped; do not change az account's global selection.
$context = Invoke-DatabaseAzureJson -Arguments @('account', 'show')
if ($context['environmentName'] -ne 'AzureCloud' -or [guid]$context['id'] -ne $SubscriptionId) {
    throw 'Subscription or Azure cloud differs from the explicitly selected target.'
}
$accountArguments = @('--resource-group', $ResourceGroupName, '--account-name', $AccountName)
$account = Invoke-DatabaseAzureJson -Arguments @(
    'cosmosdb', 'show', '--resource-group', $ResourceGroupName, '--name', $AccountName
)
$capabilities = @($account['capabilities'] | ForEach-Object { $_['name'] })
if ($account['kind'] -ne 'GlobalDocumentDB' -or 'EnableServerless' -notin $capabilities -or
    @($capabilities | Where-Object { $_ -in @('EnableMongo', 'EnableCassandra', 'EnableGremlin', 'EnableTable') }).Count -gt 0) {
    throw 'The target must be a Cosmos DB for NoSQL Serverless account. MongoDB and provisioned accounts are not supported.'
}
if ($account['consistencyPolicy']['defaultConsistencyLevel'] -ne 'Session') {
    throw 'The account must use Session consistency as specified in the design.'
}

$databaseArguments = $accountArguments + @('--database-name', $definition['databaseName'])
$databaseExists = Invoke-DatabaseAzureJson -Arguments (
    @('cosmosdb', 'sql', 'database', 'exists') + $accountArguments + @('--name', $definition['databaseName'])
)
if ($databaseExists -isnot [bool]) { throw 'Unexpected database existence response.' }
if ($databaseExists) {
    $containerExists = Invoke-DatabaseAzureJson -Arguments (
        @('cosmosdb', 'sql', 'container', 'exists') + $databaseArguments + @('--name', $definition['containerName'])
    )
    if ($containerExists -isnot [bool]) { throw 'Unexpected container existence response.' }
    if ($containerExists) {
        $container = Invoke-DatabaseAzureJson -Arguments (
            @('cosmosdb', 'sql', 'container', 'show') + $databaseArguments + @('--name', $definition['containerName'])
        )
        Assert-CompatibleContainer -Container $container
    }
}

$deploymentArguments = @(
    '--resource-group', $ResourceGroupName,
    '--name', 'rollpay-database-schema',
    '--template-file', $templatePath,
    '--parameters', "cosmosAccountName=$AccountName",
    '--mode', 'Incremental'
)

if (-not $Apply) {
    Write-Host "Preview only: $target"
    # what-if uses a human-readable renderer unless explicitly disabled, even with -o json.
    Invoke-DatabaseAzureJson -Arguments (@('deployment', 'group', 'what-if', '--no-pretty-print') + $deploymentArguments)
    return
}
if (-not $PSCmdlet.ShouldProcess($target, 'Create database/container if missing and apply indexing policy')) {
    return
}
$deployment = Invoke-DatabaseAzureJson -Arguments (@('deployment', 'group', 'create') + $deploymentArguments)
if ($deployment['properties']['provisioningState'] -ne 'Succeeded') {
    throw 'Database schema deployment did not succeed. Inspect the deployment before retrying.'
}
$container = Invoke-DatabaseAzureJson -Arguments (
    @('cosmosdb', 'sql', 'container', 'show') + $databaseArguments + @('--name', $definition['containerName'])
)
Assert-CompatibleContainer -Container $container
Write-Host "Schema applied: $target"
Write-Host 'Index transformation can continue after deployment. Verify completion and query RU before enabling dependent APIs.'
[pscustomobject]@{
    SubscriptionId = $SubscriptionId.ToString()
    ResourceGroupName = $ResourceGroupName
    AccountName = $AccountName
    DatabaseName = $definition['databaseName']
    ContainerName = $definition['containerName']
    Status = 'Succeeded'
}
