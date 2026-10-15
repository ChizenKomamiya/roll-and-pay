#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$frontendPath = Join-Path $PSScriptRoot '../../frontend'
$pnpm = if ($IsWindows) { Get-Command pnpm.cmd -ErrorAction Stop } else { Get-Command pnpm -ErrorAction Stop }
Push-Location $frontendPath
try {
    & $pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
    & $pnpm test
    if ($LASTEXITCODE -ne 0) { throw 'Frontend tests failed.' }
    & $pnpm build
    if ($LASTEXITCODE -ne 0) { throw 'Frontend typecheck/build failed.' }
}
finally { Pop-Location }
