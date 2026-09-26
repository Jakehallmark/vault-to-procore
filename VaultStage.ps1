# One stage run belongs to one Vault project. Its files directory is that project's Documents tree.
. (Join-Path $PSScriptRoot 'VaultDocumentsPath.ps1')

function Get-VaultProjectIdentity([string]$ProjectPath) {
    $ProjectPath = $ProjectPath.Trim().TrimEnd('/')
    if ($ProjectPath -notmatch '^\$/Designs/Projects/\d{6}-\d{6}/(\d{6})(?=\D|$)[^/]*$') {
        throw 'Vault project path must be one numbered project folder under a range folder.'
    }
    [pscustomobject]@{
        VaultProjectPath = $ProjectPath
        ProjectNumber = $Matches[1]
        ProjectFolderName = $ProjectPath.Split('/')[-1]
    }
}

function New-VaultStageLocation {
    param([Parameter(Mandatory = $true)][string]$OutputParent, [Parameter(Mandatory = $true)][string]$ProjectNumber)
    if ($ProjectNumber -notmatch '^\d{6}$') { throw 'A stage run requires one six-digit project number.' }
    $RunId = [guid]::NewGuid().ToString('N')
    $RunFolder = Join-Path ([IO.Path]::GetFullPath($OutputParent)) ($ProjectNumber + '-' + $RunId)
    if (Test-Path -LiteralPath $RunFolder) { throw 'Stage run folder already exists.' }
    $ContentRoot = Join-Path $RunFolder 'files'
    $CacheRoot = Join-Path $RunFolder 'download-cache'
    [pscustomobject]@{
        RunId = $RunId
        RunFolder = $RunFolder
        ContentRoot = $ContentRoot
        CacheRoot = $CacheRoot
        ManifestPath = (Join-Path $RunFolder 'transfer-report.json')
    }
}

function Resolve-ProcoreProjectLink {
    param([Parameter(Mandatory = $true)][string]$ComparisonReport, [Parameter(Mandatory = $true)][string]$ProjectNumber)
    $Report = Get-Content -LiteralPath $ComparisonReport -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$Report.CompanyId -notmatch '^[0-9]+$') { throw 'Comparison report has no company id.' }
    $Rows = @($Report.Matches | Where-Object { $_.Number -ceq $ProjectNumber })
    if ($Rows.Count -ne 1 -or [string]$Rows[0].Status -cne 'Exact number match') {
        throw 'This Vault project is not one exact Procore number match, so its files cannot be staged for that project.'
    }
    $Projects = @($Rows[0].ProcoreProjects)
    if ($Projects.Count -ne 1 -or [string]$Projects[0].ProjectId -notmatch '^[0-9]+$') {
        throw 'This Vault project does not point at one Procore project.'
    }
    [pscustomobject]@{
        ProcoreCompanyId = [string]$Report.CompanyId
        ProcoreProjectId = [string]$Projects[0].ProjectId
        ProcoreProjectName = [string]$Projects[0].Name
    }
}

function Add-VaultStageEvent($Item, [string]$Status, [string]$ErrorMessage = '') {
    if ($Status -notin @('Selected', 'Downloading', 'Staged', 'Failed')) { throw 'Unknown stage status.' }
    $Item.Status = $Status
    $Item.Error = $ErrorMessage
    [void]$Item.History.Add([pscustomobject]@{ Utc = [DateTime]::UtcNow.ToString('o'); Status = $Status })
}

function New-VaultStageFile {
    param($Identity, $Stage, [string]$VaultPath, [string]$FileId, [int]$Version)
    if ($FileId -notmatch '^[0-9]+$') { throw 'Vault file id is missing.' }
    if ($Version -lt 1) { throw 'Vault file version is missing.' }
    $DocumentsPath = Get-VaultDocumentsRelativePath -ProjectPath $Identity.VaultProjectPath -VaultFilePath $VaultPath
    $Destination = [IO.Path]::GetFullPath((Join-Path $Stage.ContentRoot ($DocumentsPath.Replace('/', '\'))))
    Assert-VaultStageFile -Identity $Identity -Stage $Stage -VaultPath $VaultPath -DocumentsPath $DocumentsPath -Destination $Destination
    $Item = [pscustomobject]@{
        ProjectNumber = $Identity.ProjectNumber
        VaultProjectPath = $Identity.VaultProjectPath
        VaultPath = $VaultPath
        DocumentsPath = $DocumentsPath
        FileId = $FileId
        Version = $Version
        Destination = $Destination
        Status = 'Selected'
        Error = ''
        SHA256 = ''
        History = (New-Object 'System.Collections.Generic.List[object]')
    }
    Add-VaultStageEvent $Item 'Selected'
    return $Item
}

function Assert-VaultStageCache {
    param([string]$CacheRoot, [string]$ContentRoot, [string]$CacheFolder)
    $CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
    $ContentRoot = [IO.Path]::GetFullPath($ContentRoot)
    $CacheFolder = [IO.Path]::GetFullPath($CacheFolder)
    if (-not $CacheFolder.StartsWith($CacheRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Download cache escaped this project run.'
    }
    if ($CacheFolder.StartsWith($ContentRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $ContentRoot.StartsWith($CacheFolder + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Download cache must stay outside the staged project files.'
    }
}

function Assert-VaultStageFile {
    param($Identity, $Stage, [string]$VaultPath, [string]$DocumentsPath, [string]$Destination)
    $Expected = Get-VaultDocumentsRelativePath -ProjectPath $Identity.VaultProjectPath -VaultFilePath $VaultPath
    if ($Expected -cne $DocumentsPath) { throw 'Documents path does not belong to this Vault project.' }
    $ContentRoot = [IO.Path]::GetFullPath($Stage.ContentRoot)
    $Target = [IO.Path]::GetFullPath($Destination)
    $RunFolder = [IO.Path]::GetFullPath($Stage.RunFolder)
    if (-not $ContentRoot.Equals((Join-Path $RunFolder 'files'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staged files must stay in this project run.'
    }
    if (-not $Target.StartsWith($ContentRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staged file escaped this project run.'
    }
    $Relative = $Target.Substring($ContentRoot.Length + 1).Replace('\', '/')
    if ($Relative -cne $DocumentsPath) { throw 'Staged path does not match the tracked Documents path.' }
    $ProjectRoot = Join-Path $ContentRoot $Identity.ProjectFolderName
    if ($Target.Equals($ProjectRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $Target.StartsWith($ProjectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Vault project folder is the Procore project and is not created in Documents.'
    }
}

function Get-StageItems($Value) {
    $Items = New-Object 'System.Collections.Generic.List[object]'
    if ($null -eq $Value) { return $Items }
    $Many = $Value -is [System.Collections.IEnumerable] -and $Value -isnot [string] -and $Value -isnot [pscustomobject]
    if ($Many) { foreach ($Entry in $Value) { [void]$Items.Add($Entry) } }
    else { [void]$Items.Add($Value) }
    return $Items
}

function Get-VaultStageFolders($Files) {
    $Folders = New-Object 'System.Collections.Generic.List[string]'
    foreach ($File in (Get-StageItems $Files)) {
        $Parts = @([string]$File.DocumentsPath -split '/')
        for ($Index = 1; $Index -lt $Parts.Count; $Index++) {
            $Folder = ($Parts[0..($Index - 1)] -join '/')
            if (-not $Folders.Contains($Folder)) { [void]$Folders.Add($Folder) }
        }
    }
    $Sorted = $Folders.ToArray()
    [Array]::Sort($Sorted, [StringComparer]::Ordinal)
    return $Sorted
}

function Get-JsonArray($Items) {
    $Items = Get-StageItems $Items
    if ($Items.Count -eq 0) { return '[]' }
    $Parts = New-Object 'System.Collections.Generic.List[string]'
    foreach ($Item in $Items) { [void]$Parts.Add((ConvertTo-Json -InputObject $Item -Depth 3 -Compress)) }
    return '[' + ($Parts.ToArray() -join ',') + ']'
}

function Get-VaultStageManifestJson($Manifest) {
    $Files = Get-StageItems $Manifest.Files
    $FileJson = New-Object 'System.Collections.Generic.List[string]'
    foreach ($File in $Files) {
        $History = Get-JsonArray $File.History
        $Body = $File | Select-Object ProjectNumber, VaultProjectPath, VaultPath, DocumentsPath, FileId, Version, Destination, Status, Error, SHA256
        $Json = ConvertTo-Json -InputObject $Body -Depth 4 -Compress
        if (-not $Json.EndsWith('}')) { throw 'Could not write the stage manifest.' }
        [void]$FileJson.Add($Json.Substring(0, $Json.Length - 1) + ',"History":' + $History + '}')
    }
    $Head = [pscustomobject]@{
        SchemaVersion = 1
        RunId = [string]$Manifest.RunId
        RunStatus = [string]$Manifest.RunStatus
        VaultProjectPath = [string]$Manifest.VaultProjectPath
        ProjectNumber = [string]$Manifest.ProjectNumber
        ProjectFolderName = [string]$Manifest.ProjectFolderName
        ProcoreCompanyId = $Manifest.ProcoreCompanyId
        ProcoreProjectId = $Manifest.ProcoreProjectId
        ProcoreProjectName = $Manifest.ProcoreProjectName
        CreatedUtc = [string]$Manifest.CreatedUtc
        UpdatedUtc = [string]$Manifest.UpdatedUtc
        ContentRoot = [string]$Manifest.ContentRoot
        InaccessibleExcluded = [int]$Manifest.InaccessibleExcluded
    }
    $HeadJson = ConvertTo-Json -InputObject $Head -Depth 4 -Compress
    if (-not $HeadJson.EndsWith('}')) { throw 'Could not write the stage manifest.' }
    $FoldersJson = Get-JsonArray (Get-VaultStageFolders $Files)
    $FilesJson = if ($FileJson.Count -eq 0) { '[]' } else { '[' + ($FileJson.ToArray() -join ',') + ']' }
    return $HeadJson.Substring(0, $HeadJson.Length - 1) + ',"Folders":' + $FoldersJson + ',"Files":' + $FilesJson + '}'
}

function Save-VaultStageManifest($Manifest, [string]$Path) {
    $Manifest.UpdatedUtc = [DateTime]::UtcNow.ToString('o')
    $Json = Get-VaultStageManifestJson $Manifest
    $Directory = [IO.Path]::GetDirectoryName($Path)
    if (-not (Test-Path -LiteralPath $Directory)) { [void][IO.Directory]::CreateDirectory($Directory) }
    $Temp = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($Temp, $Json, (New-Object Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    [IO.File]::Move($Temp, $Path)
}

function Assert-VaultStageManifest {
    param($Manifest, [switch]$VerifyFiles)
    if ([int]$Manifest.SchemaVersion -ne 1) { throw 'Unknown stage manifest.' }
    $Identity = Get-VaultProjectIdentity ([string]$Manifest.VaultProjectPath)
    if ($Identity.ProjectNumber -cne [string]$Manifest.ProjectNumber -or
        $Identity.ProjectFolderName -cne [string]$Manifest.ProjectFolderName) {
        throw 'Stage manifest project identity does not match its Vault path.'
    }
    if ([string]$Manifest.RunId -notmatch '^[0-9a-f]{32}$') { throw 'Stage run id is invalid.' }
    $ContentRoot = [IO.Path]::GetFullPath([string]$Manifest.ContentRoot)
    $RunFolder = [IO.Path]::GetDirectoryName($ContentRoot)
    if ([IO.Path]::GetFileName($RunFolder) -cne ($Identity.ProjectNumber + '-' + $Manifest.RunId)) {
        throw 'Stage folder belongs to a different project run.'
    }
    if (-not $ContentRoot.Equals((Join-Path $RunFolder 'files'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staged files must stay in this project run.'
    }
    $Stage = [pscustomobject]@{ RunFolder = $RunFolder; ContentRoot = $ContentRoot }
    $SeenIds = New-Object 'System.Collections.Generic.HashSet[string]'
    $SeenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $Files = Get-StageItems $Manifest.Files
    foreach ($File in $Files) {
        if ([string]$File.ProjectNumber -cne $Identity.ProjectNumber -or [string]$File.VaultProjectPath -cne $Identity.VaultProjectPath) {
            throw 'A staged file belongs to a different Vault project.'
        }
        if (-not $SeenIds.Add([string]$File.FileId)) { throw 'Duplicate Vault file in this project run.' }
        if (-not $SeenPaths.Add([string]$File.DocumentsPath)) { throw 'Two files in this project use the same Documents path.' }
        Assert-VaultStageFile -Identity $Identity -Stage $Stage -VaultPath ([string]$File.VaultPath) -DocumentsPath ([string]$File.DocumentsPath) -Destination ([string]$File.Destination)
        $History = New-Object 'System.Collections.Generic.List[string]'
        foreach ($Event in (Get-StageItems $File.History)) { [void]$History.Add([string]$Event.Status) }
        $Expected = New-Object 'System.Collections.Generic.List[string]'
        switch ([string]$File.Status) {
            'Selected' { [void]$Expected.Add('Selected') }
            'Downloading' { [void]$Expected.Add('Selected'); [void]$Expected.Add('Downloading') }
            'Staged' { [void]$Expected.Add('Selected'); [void]$Expected.Add('Downloading'); [void]$Expected.Add('Staged') }
            'Failed' { [void]$Expected.Add('Selected'); [void]$Expected.Add('Downloading'); [void]$Expected.Add('Failed') }
            default { throw 'Unknown file status.' }
        }
        if ($History.Count -ne $Expected.Count) { throw 'File history does not track this project from selection through its current status.' }
        for ($Index = 0; $Index -lt $Expected.Count; $Index++) {
            if ($History[$Index] -cne $Expected[$Index]) {
                throw 'File history does not track this project from selection through its current status.'
            }
        }
        $Exists = Test-Path -LiteralPath ([string]$File.Destination) -PathType Leaf
        if ($VerifyFiles) {
            if ([string]$File.Status -eq 'Staged') {
                if (-not $Exists -or [string]::IsNullOrWhiteSpace([string]$File.SHA256)) { throw 'Staged file is missing from its project run.' }
                if ((Get-FileHash -LiteralPath ([string]$File.Destination) -Algorithm SHA256).Hash -cne [string]$File.SHA256) {
                    throw 'Staged file hash does not match its project record.'
                }
            } elseif ($Exists) {
                throw 'Only staged files may be present in this project run.'
            }
        }
    }
    $StoredFolders = @($Manifest.Folders | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    $ActualFolders = @(Get-VaultStageFolders $Files)
    if ($StoredFolders.Count -ne $ActualFolders.Count) { throw 'Tracked folders do not match the staged files.' }
    for ($Index = 0; $Index -lt $ActualFolders.Count; $Index++) {
        if ($StoredFolders[$Index] -cne $ActualFolders[$Index]) { throw 'Tracked folders do not match the staged files.' }
    }
}
