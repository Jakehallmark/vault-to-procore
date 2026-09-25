param(
    [Parameter(Mandatory = $true)]$Connection,
    [string]$ProjectPath,
    [string]$OutputParent = (Join-Path $env:TEMP 'VaultTransfer-Test'),
    [string[]]$Extensions = @('.pdf', '.rdb', '.sup', '.urs', '.usw', '.wset',
        '.zap14', '.zap15', '.zap15_1', '.zap16', '.zap17', '.zap18',
        '.s7s', '.cd3', '.cd31', '.cd32')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
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
$ProjectName = $ProjectPath.Split('/')[-1]
$Run = Join-Path ([IO.Path]::GetFullPath($OutputParent)) ([guid]::NewGuid().ToString('N'))
$ContentRoot = Join-Path $Run 'files'
$ProjectLocal = Join-Path $ContentRoot $ProjectName
$Scratch = Join-Path $Run 'download-cache'
$Plan = New-Object 'System.Collections.Generic.List[object]'
$Queue = New-Object 'System.Collections.Generic.Stack[object]'
$Seen = New-Object 'System.Collections.Generic.HashSet[long]'
$Targets = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
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
        $Relative = ($FolderPath + '/' + $File.Name).Substring($ProjectPath.Length + 1)
        foreach ($Part in $Relative.Split('/')) { Assert-LocalName $Part }
        $Target = [IO.Path]::GetFullPath((Join-Path $ProjectLocal $Relative.Replace('/', '\')))
        if (-not $Target.StartsWith($ProjectLocal + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid destination.' }
        if (-not $Targets.Add($Target)) { throw ('Duplicate destination: ' + $Target) }
        $Plan.Add([pscustomobject]@{
            VaultPath = $FolderPath + '/' + $File.Name
            FileId = [string]$File.Id; Version = [int]$File.VerNum
            Destination = $Target; Status = 'Pending'; Error = ''; SHA256 = ''
            VaultFile = $File
        })
    }
    foreach ($Child in $Documents.GetFoldersByParentId($Folder.Id, $false)) {
        if ($null -ne $Child) { $Queue.Push($Child) }
    }
}
if ($Plan.Count -eq 0) { Write-Host ('No selected files. Inaccessible records: ' + $Blocked); return }
[void][IO.Directory]::CreateDirectory($Run)
$Report = Join-Path $Run 'transfer-report.json'
function Save-Report {
    ConvertTo-Json -InputObject @($Plan.ToArray() | Select-Object VaultPath, FileId, Version, Destination, Status, Error, SHA256) -Depth 4 |
        Set-Content -LiteralPath $Report -Encoding UTF8
}
Save-Report
$Plan | Select-Object VaultPath, Version | Format-Table -AutoSize -Wrap | Out-Host
Write-Host ('Selected files: ' + $Plan.Count + '; inaccessible records excluded: ' + $Blocked)
Write-Host ('Destination: ' + $ProjectLocal)
Write-Host ('Full review list: ' + $Report)
if ((Read-Host 'Review the list. Type DOWNLOAD to copy these versions, or Enter to cancel') -cne 'DOWNLOAD') { return }

$Download = [Autodesk.DataManagement.Client.Framework.Vault.Settings.AcquireFilesSettings+AcquisitionOption]::Download
foreach ($Item in $Plan) {
    try {
        $Item.Status = 'Downloading'
        Save-Report
        # Download separately to an isolated cache; create project folders only after success.
        $CacheFolder = Join-Path $Scratch $Item.FileId
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
        $Item.SHA256 = (Get-FileHash -LiteralPath $CachedFile -Algorithm SHA256).Hash
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Item.Destination))
        [IO.File]::Move($CachedFile, $Item.Destination)
        $Item.Status = 'Copied'
        Write-Host ('Copied: ' + $Item.VaultPath)
    }
    catch {
        $Item.Status = 'Failed'
        $Item.Error = $_.Exception.Message
        Write-Warning ($Item.VaultPath + ': ' + $Item.Error)
        # Stop on first failure so an SDK incompatibility does not repeat for every file.
        break
    }
    finally { Save-Report }
}
# Only remove empty directories within this run's verified project output tree.
if (Test-Path -LiteralPath $ContentRoot) {
    Get-ChildItem -LiteralPath $ContentRoot -Directory -Recurse |
        Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
            if (@(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0) {
                [IO.Directory]::Delete($_.FullName, $false)
            }
        }
}
$Copied = @($Plan | Where-Object Status -eq 'Copied').Count
Write-Host ('Copied ' + $Copied + ' of ' + $Plan.Count + ' selected files.')
Write-Host ('Project files: ' + $ProjectLocal)
Write-Host ('Results: ' + $Report)
if ($Copied -ne $Plan.Count) { throw 'Test incomplete. Read transfer-report.json before retrying.' }
