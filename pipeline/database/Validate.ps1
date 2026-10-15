#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repoPath
try {
    pwsh -NoProfile -File ./database/tests/Test-Schema.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Database offline tests failed.' }
    az bicep install --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw 'Bicep installation failed.' }
    New-Item -ItemType Directory -Path database/build -Force | Out-Null
    az bicep build --file database/main.bicep --outfile database/build/main.json
    if ($LASTEXITCODE -ne 0) { throw 'Database compilation failed.' }
}
finally { Pop-Location }
