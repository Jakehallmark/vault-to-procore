$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# Offline contract test: no Autodesk libraries, credentials, or network calls.
$Root = '$/Designs/Projects/101000-101999/101001 - Test'
$Documents = [pscustomobject]@{}
$Documents | Add-Member ScriptMethod GetFolderByPath {
    param($Path)
    [pscustomobject]@{ Id = 1; FullName = $Path }
}
$Documents | Add-Member ScriptMethod GetFoldersByParentId {
    param($Id, $Recurse)
    if ($Id -eq 1) {
        [pscustomobject]@{ Id = 2; FullName = '$/Designs/Projects/101000-101999/101001 - Test/Empty' }
        [pscustomobject]@{ Id = 3; FullName = '$/Designs/Projects/101000-101999/101001 - Test/Drawings' }
    }
}
$Documents | Add-Member ScriptMethod GetLatestFilesByFolderId {
    param($Id, $Hidden)
    if ($Id -eq 3) { [pscustomobject]@{ Id = 42; MasterId = 21; VerNum = 2; Name = 'drawing.pdf' } }
}
$Connection = [pscustomobject]@{ WebServiceManager = [pscustomobject]@{ DocumentService = $Documents } }
$Reports = Join-Path $PSScriptRoot ('test-results\scoped-' + [guid]::NewGuid().ToString('N'))
& (Join-Path $PSScriptRoot 'ScanVaultProjects.ps1') -Connection $Connection -ScanRoot $Root -ReportFolder $Reports
$Run = Get-ChildItem -LiteralPath $Reports -Directory | Select-Object -First 1
$Summary = Get-Content -LiteralPath (Join-Path $Run.FullName 'scan-summary.json') -Raw | ConvertFrom-Json
$Folders = Get-Content -LiteralPath (Join-Path $Run.FullName 'folders.json') -Raw | ConvertFrom-Json
$Files = @(Get-Content -LiteralPath (Join-Path $Run.FullName 'inventory.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
if (-not $Summary.EnumerationFinished -or -not $Summary.Complete -or $Summary.ScanRoot -ne $Root) { throw 'Scoped scan contract failed.' }
if ($Folders.Count -ne 3 -or $Folders -notcontains ($Root + '/Empty')) { throw 'Empty folder was lost.' }
if ($Files.Count -ne 1 -or $Files[0].VaultPath -ne ($Root + '/Drawings/drawing.pdf')) { throw 'Scoped file inventory failed.' }
Write-Host 'PASS scoped Vault inventory includes files and empty folders without a live connection.'

