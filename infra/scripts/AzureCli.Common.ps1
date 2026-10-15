#Requires -Version 7.0
Set-StrictMode -Version Latest

function Invoke-RollPayAzureJson {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $command = Get-Command az -ErrorAction Stop
    $cliArguments = @($Arguments) + @('--only-show-errors', '--output', 'json')
    $global:LASTEXITCODE = 0
    $text = @(& $command @cliArguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLIが失敗しました。ログイン・Entra権限を確認してください。`n$($text -join "`n")"
    }
    $json = ($text -join "`n").Trim()
    if ($json) {
        return ConvertFrom-Json -InputObject $json -AsHashtable -ErrorAction Stop
    }
}

function Get-RollPayUserContext {
    param([Parameter(Mandatory)][guid] $ExpectedTenantId)

    $account = Invoke-RollPayAzureJson -Arguments @('account', 'show')
    # az ad系には--subscriptionを渡せないため、現在の選択値を検証する。
    # 取得スクリプトからCLIの既定サブスクリプションを変更しない。
    if ($script:RollPaySubscriptionId -and $account['id'] -ne $script:RollPaySubscriptionId) {
        throw '現在選択されているサブスクリプションが指定値と違います。先に az account set --subscription <SubscriptionId> を実行してください。'
    }
    if ($account['environmentName'] -ne 'AzureCloud') {
        throw 'このスクリプトはAzureパブリッククラウド用です。'
    }
    if ([guid]$account['tenantId'] -ne $ExpectedTenantId) {
        throw 'ログイン先のテナントが指定値と違います。az login --tenant <TenantId> とサブスクリプション選択を確認してください。'
    }
    $user = Invoke-RollPayAzureJson -Arguments @('ad', 'signed-in-user', 'show', '--query', '{id:id}')
    if (-not $user -or -not $user['id']) {
        throw 'ユーザーObject IDを取得できません。サービスプリンシパルではなく管理者本人でログインしてください。'
    }
    return @{
        TenantId = $ExpectedTenantId.ToString()
        SubscriptionId = [string]$account['id']
        AdministratorObjectId = ([guid]$user['id']).ToString()
    }
}

function Invoke-RollPayGraph {
    param(
        [ValidateSet('GET', 'POST', 'PATCH')][string] $Method = 'GET',
        [Parameter(Mandatory)][string] $Url,
        [hashtable] $Body
    )

    $uri = [uri]$Url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'graph.microsoft.com' -or -not $uri.AbsolutePath.StartsWith('/v1.0/')) {
        throw 'Microsoft Graph v1.0以外のURLは使用できません。'
    }
    $arguments = @('rest', '--method', $Method, '--url', $Url, '--resource', 'https://graph.microsoft.com/')
    $bodyPath = $null
    try {
        if ($null -ne $Body) {
            # 引数内でJSONをクォートせず、UTF-8の一時ファイルを使う。
            $bodyPath = Join-Path ([IO.Path]::GetTempPath()) ('rollpay-graph-' + [guid]::NewGuid().ToString() + '.json')
            [IO.File]::WriteAllText($bodyPath, ($Body | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
            $arguments += @('--headers', 'Content-Type=application/json', '--body', "@$bodyPath")
        }
        return Invoke-RollPayAzureJson -Arguments $arguments
    }
    finally {
        if ($bodyPath -and (Test-Path -LiteralPath $bodyPath)) {
            Remove-Item -LiteralPath $bodyPath -Force
        }
    }
}

function Get-RollPayGraphCollection {
    param([Parameter(Mandatory)][string] $Url)

    $next = $Url
    while ($next) {
        $page = Invoke-RollPayGraph -Url $next
        foreach ($item in $page['value']) { $item }
        $next = $page['@odata.nextLink']
    }
}

function Write-RollPayLocalResult {
    param([Parameter(Mandatory)][object] $Result, [string] $OutputPath)

    if ($OutputPath) {
        if (-not $OutputPath.EndsWith('.local.json', [StringComparison]::OrdinalIgnoreCase)) {
            throw '出力ファイルはGit対象外の *.local.json を指定してください。'
        }
        $fullPath = [IO.Path]::GetFullPath($OutputPath)
        [IO.File]::WriteAllText($fullPath, ($Result | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        Write-Host "ローカル設定を保存しました: $fullPath"
    }
    return $Result
}
