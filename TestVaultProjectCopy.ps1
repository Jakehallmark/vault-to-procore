param(
    [Parameter(Mandatory = $true)]$Connection,
    [string]$ProjectPath,
    [string]$ComparisonReport,
    [string]$OutputParent = (Join-Path $env:TEMP 'VaultTransfer-Test'),
    [string[]]$Extensions = @('.pdf', '.rdb', '.sup', '.urs', '.usw', '.wset',
        '.zap14', '.zap15', '.zap15_1', '.zap16', '.zap17', '.zap18',
        '.s7s', '.cd3', '.cd31', '.cd32')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'VaultStage.ps1')
if ($null -eq $Connection) { throw 'An active Vault connection is required.' }
if (-not $ProjectPath) { $ProjectPath = Read-Host 'Exact Vault project folder path (beginning $/Designs/Projects/)' }
$ProjectPath = $ProjectPath.Trim().TrimEnd('/')
if ($ProjectPath -notmatch '^\$/') {
    $ProjectPath = '$/Designs/Projects/' + $ProjectPath.TrimStart('/')
}
# Require one project folder beneath a range or another grouping folder.
if ($ProjectPath -notmatch '^\$/Designs/Projects/[^/]+/[^/]+$') {
    throw 'Choose one project folder, for example $/Designs/Projects/103000-103999/103821 - Project Name.'
}
$Documents = $Connection.WebServiceManager.DocumentService
$Root = $Documents.GetFolderByPath($ProjectPath)
if ($null -eq $Root) { throw 'Project folder was not found.' }
$ProjectPath = $Root.FullName.TrimEnd('/')
$Identity = Get-VaultProjectIdentity $ProjectPath
$ProjectName = $Identity.ProjectFolderName
$Link = $null
if ($ComparisonReport) { $Link = Resolve-ProcoreProjectLink -ComparisonReport $ComparisonReport -ProjectNumber $Identity.ProjectNumber }
$Stage = New-VaultStageLocation -OutputParent $OutputParent -ProjectNumber $Identity.ProjectNumber
# files\ is this project's Documents tree. The run folder name keeps it apart from every other project.
$ContentRoot = $Stage.ContentRoot
$Scratch = $Stage.CacheRoot
$Plan = New-Object 'System.Collections.Generic.List[object]'
$Queue = New-Object 'System.Collections.Generic.Stack[object]'
$Seen = New-Object 'System.Collections.Generic.HashSet[long]'
$Targets = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$FileIds = New-Object 'System.Collections.Generic.HashSet[string]'
$Queue.Push($Root)
$Blocked = 0

function Assert-LocalName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -in @('.', '..') -or
        $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Name.EndsWith('.') -or $Name.EndsWith(' ') -or
        $Name -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)') {
        throw ('Cannot safely reproduce Windows name: ' + $Name)
    }
}
Assert-LocalName $ProjectName
while ($Queue.Count -gt 0) {
    $Folder = $Queue.Pop()
    if (-not $Seen.Add([long]$Folder.Id)) { throw 'Repeated folder; stopped.' }
    $FolderPath = $Folder.FullName.TrimEnd('/')
    if ($FolderPath -ne $ProjectPath -and -not $FolderPath.StartsWith($ProjectPath + '/', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Vault returned a folder outside the selected project.'
    }
    Write-Host ('Scanning: ' + $FolderPath)
    foreach ($File in $Documents.GetLatestFilesByFolderId($Folder.Id, $true)) {
        if ($null -eq $File) { continue }
        if ($File.VerNum -lt 1 -or $File.Name -eq 'Inaccessible') { $Blocked++; continue }
        if ($Extensions -notcontains [IO.Path]::GetExtension($File.Name)) { continue }
        if (-not $FileIds.Add([string]$File.Id)) { throw 'Vault returned the same file twice in this project.' }
        $Item = New-VaultStageFile -Identity $Identity -Stage $Stage -VaultPath ($FolderPath + '/' + $File.Name) -FileId ([string]$File.Id) -Version ([int]$File.VerNum)
        foreach ($Part in $Item.DocumentsPath.Split('/')) { Assert-LocalName $Part }
        if (-not $Targets.Add($Item.Destination)) { throw ('Duplicate destination: ' + $Item.Destination) }
        $Item | Add-Member NoteProperty VaultFile $File
        $Plan.Add($Item)
    }
    foreach ($Child in $Documents.GetFoldersByParentId($Folder.Id, $false)) {
        if ($null -ne $Child) { $Queue.Push($Child) }
    }
}
if ($Plan.Count -eq 0) { Write-Host ('No selected files. Inaccessible records: ' + $Blocked); return }
$Manifest = [pscustomobject]@{
    SchemaVersion = 1; RunId = $Stage.RunId; RunStatus = 'Planned'
    VaultProjectPath = $Identity.VaultProjectPath; ProjectNumber = $Identity.ProjectNumber
    ProjectFolderName = $Identity.ProjectFolderName
    ProcoreCompanyId = if ($Link) { $Link.ProcoreCompanyId } else { $null }
    ProcoreProjectId = if ($Link) { $Link.ProcoreProjectId } else { $null }
    ProcoreProjectName = if ($Link) { $Link.ProcoreProjectName } else { $null }
    CreatedUtc = [DateTime]::UtcNow.ToString('o'); UpdatedUtc = [DateTime]::UtcNow.ToString('o')
    ContentRoot = $ContentRoot; InaccessibleExcluded = $Blocked; Files = $Plan
}
[void][IO.Directory]::CreateDirectory($Stage.RunFolder)
Save-VaultStageManifest $Manifest $Stage.ManifestPath
Assert-VaultStageManifest (Get-Content -LiteralPath $Stage.ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json)
$Plan | Select-Object ProjectNumber, VaultPath, DocumentsPath, Version | Format-Table -AutoSize -Wrap | Out-Host
Write-Host ('Project ' + $Identity.ProjectNumber + ' only. Selected files: ' + $Plan.Count + '; inaccessible records excluded: ' + $Blocked)
if ($Link) { Write-Host ('Procore project ' + $Link.ProcoreProjectId + ' ' + $Link.ProcoreProjectName) }
Write-Host ('Documents tree, without the Vault project folder: ' + $ContentRoot)
Write-Host ('Stage record: ' + $Stage.ManifestPath)
if ((Read-Host 'Review the list. Type DOWNLOAD to copy these versions, or Enter to cancel') -cne 'DOWNLOAD') {
    $Manifest.RunStatus = 'Cancelled'
    Save-VaultStageManifest $Manifest $Stage.ManifestPath
    return
}
$Manifest.RunStatus = 'Staging'
$Download = [Autodesk.DataManagement.Client.Framework.Vault.Settings.AcquireFilesSettings+AcquisitionOption]::Download
foreach ($Item in $Plan) {
    try {
        Add-VaultStageEvent $Item 'Downloading'
        Save-VaultStageManifest $Manifest $Stage.ManifestPath
        $CacheFolder = Join-Path $Scratch $Item.FileId
        Assert-VaultStageCache -CacheRoot $Scratch -ContentRoot $ContentRoot -CacheFolder $CacheFolder
        [void][IO.Directory]::CreateDirectory($CacheFolder)
        $Settings = New-Object Autodesk.DataManagement.Client.Framework.Vault.Settings.AcquireFilesSettings($Connection)
        $Settings.LocalPath = New-Object Autodesk.DataManagement.Client.Framework.Currency.FolderPathAbsolute($CacheFolder)
        $Settings.OrganizeFilesRelativeToCommonVaultRoot = $false
        $Relations = $Settings.OptionsRelationshipGathering.FileRelationshipSettings
        $Relations.IncludeChildren = $false
        $Relations.IncludeParents = $false
        $Relations.IncludeAttachments = $false
        $Relations.IncludeRelatedDocumentation = $false
        $Iteration = New-Object Autodesk.DataManagement.Client.Framework.Vault.Currency.Entities.FileIteration($Connection, $Item.VaultFile)
        $Settings.AddFileToAcquire($Iteration, $Download)
        $Result = $Connection.FileManager.AcquireFiles($Settings)
        $Results = @($Result.FileResults)
        if ($Results.Count -ne 1 -or [string]$Results[0].Status -ne 'Success') {
            throw 'Vault did not report exactly one successful download. See the cache for any partial file.'
        }
        $CachedFile = Join-Path $CacheFolder $Item.VaultFile.Name
        if (-not (Test-Path -LiteralPath $CachedFile -PathType Leaf)) { throw 'Expected downloaded file is missing.' }
        if ((Get-Item -LiteralPath $CachedFile).Length -ne [long]$Item.VaultFile.FileSize) { throw 'Downloaded size differs from Vault metadata.' }
        Assert-VaultStageFile -Identity $Identity -Stage $Stage -VaultPath $Item.VaultPath -DocumentsPath $Item.DocumentsPath -Destination $Item.Destination
        $Item.SHA256 = (Get-FileHash -LiteralPath $CachedFile -Algorithm SHA256).Hash
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Item.Destination))
        [IO.File]::Move($CachedFile, $Item.Destination)
        Add-VaultStageEvent $Item 'Staged'
        Write-Host ('Staged: ' + $Item.VaultPath)
    }
    catch {
        if ($Item.Status -eq 'Downloading') { Add-VaultStageEvent $Item 'Failed' $_.Exception.Message }
        else { $Item.Status = 'Failed'; $Item.Error = $_.Exception.Message }
        $Manifest.RunStatus = 'Failed'
        Write-Warning ($Item.VaultPath + ': ' + $Item.Error)
        break
    }
    finally { Save-VaultStageManifest $Manifest $Stage.ManifestPath }
}
$ContentFull = [IO.Path]::GetFullPath($ContentRoot)
if (Test-Path -LiteralPath $ContentRoot) {
    Get-ChildItem -LiteralPath $ContentRoot -Directory -Recurse |
        Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
            if (-not $_.FullName.StartsWith($ContentFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Cleanup left this project run.'
            }
            if (@(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0) {
                [IO.Directory]::Delete($_.FullName, $false)
            }
        }
}
$Staged = @($Plan | Where-Object Status -eq 'Staged').Count
if ($Staged -eq $Plan.Count) { $Manifest.RunStatus = 'Staged' }
Save-VaultStageManifest $Manifest $Stage.ManifestPath
$Saved = Get-Content -LiteralPath $Stage.ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-VaultStageManifest $Saved -VerifyFiles
Write-Host ('Staged ' + $Staged + ' of ' + $Plan.Count + ' selected files for project ' + $Identity.ProjectNumber + '.')
Write-Host ('Documents tree: ' + $ContentRoot)
Write-Host ('Stage record: ' + $Stage.ManifestPath)
if ($Staged -ne $Plan.Count) { throw 'Test incomplete. Read transfer-report.json before retrying.' }
