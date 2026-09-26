$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'VaultStage.ps1')
$Project = '$/Designs/Projects/101000-101999/101097 - WM Wave 23 - Walmart Store 2151 1250kw'
$OtherProject = '$/Designs/Projects/101000-101999/101083 - WM Wave 23 - Walmart Store 1349 1250kw'
$Identity = Get-VaultProjectIdentity $Project
if ($Identity.ProjectNumber -cne '101097') { throw 'Project number was not taken from the Vault folder.' }
$Rejected = $false
try { Get-VaultProjectIdentity '$/Designs/Projects/other/5112 - 150001DA-NBD' } catch { $Rejected = $true }
if (-not $Rejected) { throw 'A project without the numbered Vault folder was accepted.' }

$Parent = Join-Path ([IO.Path]::GetTempPath()) ('vault-stage-' + [guid]::NewGuid().ToString('N'))
$Stage = New-VaultStageLocation -OutputParent $Parent -ProjectNumber $Identity.ProjectNumber
$Other = New-VaultStageLocation -OutputParent $Parent -ProjectNumber '101083'
if ($Stage.RunFolder.StartsWith($Other.RunFolder) -or $Other.RunFolder.StartsWith($Stage.RunFolder)) {
    throw 'Two project runs share a folder.'
}
if (-not $Stage.RunFolder.EndsWith('\101097-' + $Stage.RunId)) { throw 'Run folder is not bound to project 101097.' }

$Item = New-VaultStageFile -Identity $Identity -Stage $Stage -VaultPath ($Project + '/Settings Files/PLC/PowerBlock 1/relay.zap15') -FileId '42' -Version 3
if ($Item.DocumentsPath -cne 'Settings Files/PLC/PowerBlock 1/relay.zap15') { throw 'Documents path lost the child folders.' }
if ($Item.History[0].Status -cne 'Selected') { throw 'File did not start as Selected.' }
$Foreign = $false
try {
    Assert-VaultStageFile -Identity $Identity -Stage $Stage -VaultPath ($OtherProject + '/Settings Files/PLC/relay.zap15') -DocumentsPath 'Settings Files/PLC/relay.zap15' -Destination $Item.Destination
} catch { $Foreign = $true }
if (-not $Foreign) { throw 'A file from another Vault project was accepted into this run.' }

$Manifest = [pscustomobject]@{
    SchemaVersion = 1; RunId = $Stage.RunId; RunStatus = 'Planned'
    VaultProjectPath = $Identity.VaultProjectPath; ProjectNumber = $Identity.ProjectNumber
    ProjectFolderName = $Identity.ProjectFolderName
    ProcoreCompanyId = '12233'; ProcoreProjectId = '2460697'; ProcoreProjectName = 'FY23 WM 2151 Sunrise, FL'
    CreatedUtc = [DateTime]::UtcNow.ToString('o'); UpdatedUtc = [DateTime]::UtcNow.ToString('o')
    ContentRoot = $Stage.ContentRoot; InaccessibleExcluded = 0; Files = @($Item)
}
Save-VaultStageManifest $Manifest $Stage.ManifestPath
$Raw = [IO.File]::ReadAllText($Stage.ManifestPath)
if ($Raw -notmatch '"Files":\[' -or $Raw -notmatch '"History":\[' -or $Raw -notmatch '"Folders":\[') {
    throw 'A one-file stage record did not keep arrays.'
}
$Saved = Get-Content -LiteralPath $Stage.ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-VaultStageManifest $Saved
if ($Saved.ProjectNumber -cne '101097' -or $Saved.ProcoreProjectId -cne '2460697') { throw 'Stage record lost its project link.' }
$Saved.Files = @($Saved.Files)
$Saved.Files[0].ProjectNumber = '101083'
$Mixed = $false
try { Assert-VaultStageManifest $Saved } catch { $Mixed = $true }
if (-not $Mixed) { throw 'Changing a file to another project number was accepted.' }

Add-VaultStageEvent $Item 'Downloading'
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Item.Destination))
[IO.File]::WriteAllText($Item.Destination, 'relay')
$Item.SHA256 = (Get-FileHash -LiteralPath $Item.Destination -Algorithm SHA256).Hash
Add-VaultStageEvent $Item 'Staged'
$Manifest.RunStatus = 'Staged'
$Manifest.Files = @($Item)
Save-VaultStageManifest $Manifest $Stage.ManifestPath
Assert-VaultStageManifest (Get-Content -LiteralPath $Stage.ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json) -VerifyFiles

$Comparison = Join-Path $Parent 'comparison.json'
@{
    CompanyId = '12233'
    Matches = @(
        @{ Number = '101097'; Status = 'Exact number match'; ProcoreProjects = @(@{ ProjectId = '2460697'; Name = 'FY23 WM 2151 Sunrise, FL' }) },
        @{ Number = '101646'; Status = 'Needs review: duplicate number'; ProcoreProjects = @(@{ ProjectId = '1'; Name = 'A' }, @{ ProjectId = '2'; Name = 'B' }) }
    )
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Comparison -Encoding UTF8
$Link = Resolve-ProcoreProjectLink -ComparisonReport $Comparison -ProjectNumber '101097'
if ($Link.ProcoreProjectId -cne '2460697') { throw 'Exact Procore project was not recorded.' }
$Duplicate = $false
try { Resolve-ProcoreProjectLink -ComparisonReport $Comparison -ProjectNumber '101646' } catch { $Duplicate = $true }
if (-not $Duplicate) { throw 'A duplicate Procore number was accepted for staging.' }

$Tokens = $null
$Errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'TestVaultProjectCopy.ps1'), [ref]$Tokens, [ref]$Errors)
if ($Errors.Count -ne 0) { throw ('Project copy script did not parse: ' + $Errors[0].ToString()) }
Remove-Item -LiteralPath $Parent -Recurse -Force
Write-Host 'PASS: each stage run stays with one project and records every file from selection through staging.'
