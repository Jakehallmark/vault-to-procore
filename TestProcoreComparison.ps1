$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ProcoreSandbox.Core.ps1')
$Root = Join-Path $PSScriptRoot ('test-results/metadata-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($Root)
$ApiPath=Join-Path $Root 'api.json'; $VaultPath=Join-Path $Root 'vault.json'
$Projects = @(
    [pscustomobject]@{CompanyId='12233';ProjectId='1';Number='001234';Name='Exact';Active=$false},
    [pscustomobject]@{CompanyId='12233';ProjectId='2';Number='002345';Name='Duplicate A';Active=$true},
    [pscustomobject]@{CompanyId='12233';ProjectId='3';Number='002345';Name='Duplicate B';Active=$true},
    [pscustomobject]@{CompanyId='12233';ProjectId='4';Number=$null;Name='Missing';Active=$null},
    [pscustomobject]@{CompanyId='12233';ProjectId='5';Number='1234';Name='No numeric coercion';Active=$true},
    [pscustomobject]@{CompanyId='12233';ProjectId='6';Number='003456';Name='Duplicate Vault paths';Active=$true}
)
$Api=[ordered]@{Environment='Production';CompanyId='12233';EnumerationFinished=$true;DetailsFinished=$true;DetailsFetched=6;Projects=$Projects;Error=$null}
$Prior=@{Source='Portfolio CSV';CompanyName='PowerSecure, Inc.';ProjectCount=1;VaultSHA256='fixture';ImportedUtc='2026-09-16';Matches=@(
    @{Number='001234';VaultPaths=@('$/Designs/Projects/001000-001999/001234 - A')},
    @{Number='002345';VaultPaths=@('$/Designs/Projects/002000-002999/002345 - B')},
    @{Number='003456';VaultPaths=@('$/Designs/Projects/003000-003999/003456 - C','$/Designs/Projects/003000-003999/003456 - D')},
    @{Number='004567';VaultPaths=@('$/Designs/Projects/004000-004999/004567 - E')}
)}
$Api | ConvertTo-Json -Depth 8 | Set-Content $ApiPath
$Prior | ConvertTo-Json -Depth 8 | Set-Content $VaultPath
& (Join-Path $PSScriptRoot 'CompareProcoreMetadata.ps1') -ProcoreReport $ApiPath -VaultSnapshot $VaultPath -OutputFolder $Root
$ResultPath = @(Get-ChildItem $Root -Recurse -Filter comparison.json)[0].FullName
$Result = Get-Content $ResultPath -Raw | ConvertFrom-Json
if (@($Result.Matches | Where-Object Status -eq 'Exact number match').Count -ne 1 -or
    @($Result.Matches | Where-Object Status -eq 'Needs review: duplicate number').Count -ne 2 -or
    @($Result.Matches | Where-Object Status -eq 'Missing Procore number').Count -ne 1 -or
    @($Result.Matches | ForEach-Object { $_.ProcoreProjects }).Count -ne 6) { throw 'Comparison classification or row preservation failed.' }
if (@($Result.Matches | Where-Object Number -ceq '001234')[0].ProcoreProjects[0].Active -ne $false) { throw 'Inactive status lost.' }
$Api.DetailsFinished=$false
$Api | ConvertTo-Json -Depth 8 | Set-Content $ApiPath
$Rejected=$false
try { & (Join-Path $PSScriptRoot 'CompareProcoreMetadata.ps1') -ProcoreReport $ApiPath -VaultSnapshot $VaultPath -OutputFolder $Root } catch { $Rejected=$true }
if (-not $Rejected) { throw 'Partial metadata was accepted.' }
Save-ProcoreCheckpoint $Api $Root
$Api.DetailsFetched=2
Save-ProcoreCheckpoint $Api $Root
$Saved=Get-Content (Join-Path $Root 'projects.partial.json') -Raw | ConvertFrom-Json
if ($Saved.DetailsFetched -ne 2 -or $Saved.DetailsFinished) { throw 'Checkpoint replacement failed.' }
Get-ChildItem $PSScriptRoot -Filter '*Procore*.ps1' | ForEach-Object {
    $Tokens=$null; $Errors=$null
    $null=[System.Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$Tokens,[ref]$Errors)
    if ($Errors.Count) { throw ('Syntax error: ' + $_.Name) }
}
Write-Host 'PASS: exact matching, both duplicate cases, missing numbers, leading zeros, inactive status, partial rejection, checkpoint replacement, and script syntax.'
