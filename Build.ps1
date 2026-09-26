$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Set-Location $PSScriptRoot

$Output = Join-Path $PSScriptRoot 'dist\win-x64'
$Exe = Join-Path $Output 'VaultTransfer.exe'
$Running = Get-Process -Name VaultTransfer -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $Exe }
if ($Running) {
    throw 'Vault Transfer is running from dist\win-x64. Exit it from the tray, then build again.'
}
& dotnet @(
    'publish', (Join-Path $PSScriptRoot 'VaultTransfer.csproj'),
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    "-p:OutputPath=$(Join-Path $PSScriptRoot 'obj\publish')",
    '-o', $Output,
    '--source', 'https://api.nuget.org/v3/index.json'
)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$Required = @(
    'VaultTransfer.exe',
    'VaultConnectionCheck.ps1',
    'ScanVaultProjects.ps1',
    'ConnectProcoreProduction.ps1',
    'ProcoreProduction.Runtime.ps1',
    'ProcoreSandbox.Core.ps1'
)
$Missing = @($Required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Output $_)) })
if ($Missing.Count -gt 0) {
    throw ('Build finished without: ' + ($Missing -join ', '))
}
Write-Host ('Built ' + (Join-Path $Output 'VaultTransfer.exe'))
