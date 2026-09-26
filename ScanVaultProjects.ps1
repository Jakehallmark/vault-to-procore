param(
    [Parameter(Mandatory = $true)]$Connection,
    [string]$ScanRoot = '$/Designs/Projects',
    [string]$ReportFolder = (Join-Path $PSScriptRoot 'reports'),
    [string]$ChangedSince = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($null -eq $Connection) { throw 'An active Vault connection is required.' }
if (-not $ScanRoot.StartsWith('$/')) { throw 'Use a Vault path beginning with $/.' }

$Documents = $Connection.WebServiceManager.DocumentService
$StartFolder = $Documents.GetFolderByPath($ScanRoot)
if ($null -eq $StartFolder) { throw ('Vault folder was not found: ' + $ScanRoot) }

$RunFolder = Join-Path $ReportFolder ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($RunFolder)
$CsvPath = Join-Path $RunFolder 'inventory.partial.csv'
$JsonPath = Join-Path $RunFolder 'inventory.partial.jsonl'
$Csv = $null
$Json = $null
$Folders = New-Object 'System.Collections.Generic.Stack[object]'
$Seen = New-Object 'System.Collections.Generic.HashSet[long]'
$Folders.Push($StartFolder)
$FileCount = 0
$FolderCount = 0
$Finished = $false
$Failure = 'Interrupted before completion.'
$Started = [DateTime]::UtcNow.ToString('o')
$ScanMode = 'Full'
$Complete = $true

function Write-InventoryRecord {
    param($Record)
    $script:Json.WriteLine(($Record | ConvertTo-Json -Compress))
    $Cells = foreach ($Value in @($Record.VaultPath, $Record.FileName, $Record.FileId, $Record.MasterId, $Record.Version, $Record.Decision)) {
        '"' + ("'" + [string]$Value).Replace('"', '""') + '"'
    }
    $script:Csv.WriteLine(($Cells -join ','))
    $script:FileCount++
}

function Search-VaultCheckedInSince {
    param([DateTime]$When, [switch]$FirstPageOnly)
    $Defs = $Connection.WebServiceManager.PropertyService.GetPropertyDefinitionsByEntityClassId('FILE')
    $CheckIn = $null
    foreach ($Def in $Defs) {
        if ($Def.SysName -eq 'CheckInDate') { $CheckIn = $Def; break }
    }
    if ($null -eq $CheckIn) { throw 'Vault has no CheckInDate property.' }
    $Cond = New-Object Autodesk.Connectivity.WebServices.SrchCond
    $Cond.PropDefId = $CheckIn.Id
    $Cond.SrchOper = [Autodesk.Connectivity.WebServices.SrchOper]::GreaterThanOrEqualTo
    $Cond.SrchTxt = $When.ToString('MM/dd/yyyy HH:mm:ss')
    $Cond.PropTyp = 'SingleProperty'
    $Cond.SrchRule = 'Must'
    $Conditions = New-Object 'Autodesk.Connectivity.WebServices.SrchCond[]' 1
    $Conditions[0] = $Cond
    $FolderIds = New-Object 'long[]' 1
    $FolderIds[0] = [long]$StartFolder.Id
    $Bookmark = ''
    $Status = New-Object Autodesk.Connectivity.WebServices.SrchStatus
    $Found = New-Object System.Collections.Generic.List[object]
    $FolderNames = @{}
    do {
        $Page = $Documents.FindFilesBySearchConditions($Conditions, $null, $FolderIds, $true, $true, [ref]$Bookmark, [ref]$Status)
        if ($null -eq $Page) { break }
        foreach ($VaultFile in $Page) {
            if ($null -eq $VaultFile) { continue }
            if ($FirstPageOnly) { return $true }
            $FolderKey = [string]$VaultFile.FolderId
            if (-not $FolderNames.ContainsKey($FolderKey)) {
                $Parent = $Documents.GetFolderById([long]$VaultFile.FolderId)
                if ($null -eq $Parent) { throw 'Vault folder was not found for a changed file.' }
                $FolderNames[$FolderKey] = [string]$Parent.FullName
            }
            $Found.Add([pscustomobject][ordered]@{
                VaultPath = $FolderNames[$FolderKey].TrimEnd('/') + '/' + $VaultFile.Name
                FileName = [string]$VaultFile.Name
                FileId = [string]$VaultFile.Id
                MasterId = [string]$VaultFile.MasterId
                Version = [int]$VaultFile.VerNum
                Decision = 'Pending'
            })
        }
        if ($FirstPageOnly) { return $false }
    } while ($Bookmark)
    $script:FolderCount = $FolderNames.Count
    return $Found.ToArray()
}

function Get-ChangedVaultFiles {
    $Probe = Search-VaultCheckedInSince -When ([DateTime]'2000-01-01') -FirstPageOnly
    if ($Probe -ne $true) { throw 'Vault check-in search did not return a known file.' }
    $Since = [DateTimeOffset]::Parse($ChangedSince, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
    return Search-VaultCheckedInSince -When $Since.ToLocalTime().DateTime
}

try {
    $Csv = New-Object IO.StreamWriter($CsvPath, $false, [Text.Encoding]::UTF8)
    $Json = New-Object IO.StreamWriter($JsonPath, $false, [Text.Encoding]::UTF8)
    $Csv.WriteLine('VaultPath,FileName,FileId,MasterId,Version,Decision')
    $IncrementalReady = $false
    $ChangedFiles = $null
    if ($ChangedSince) {
        try {
            $ChangedFiles = Get-ChangedVaultFiles
            $IncrementalReady = $true
        }
        catch {
            Write-Host ('Changed-file search was not used: ' + $_.Exception.Message)
        }
    }
    if ($IncrementalReady) {
        $ScanMode = 'Incremental'
        $Complete = $false
        if ($null -ne $ChangedFiles) {
            foreach ($Record in $ChangedFiles) { Write-InventoryRecord $Record }
        }
    }
    else {
    while ($Folders.Count -gt 0) {
        $Folder = $Folders.Pop()
        if (-not $Seen.Add([long]$Folder.Id)) { throw 'A folder was returned twice; scan stopped for review.' }
        Write-Host ('Scanning: ' + $Folder.FullName)
        # Include hidden file records returned to this authenticated user.
        foreach ($VaultFile in $Documents.GetLatestFilesByFolderId($Folder.Id, $true)) {
            if ($null -eq $VaultFile) { continue }
            $Record = [pscustomobject][ordered]@{
                VaultPath = $Folder.FullName.TrimEnd('/') + '/' + $VaultFile.Name
                FileName = [string]$VaultFile.Name
                FileId = [string]$VaultFile.Id
                MasterId = [string]$VaultFile.MasterId
                Version = [int]$VaultFile.VerNum
                Decision = 'Pending'
            }
            # Spreadsheet text prefixes prevent names from being treated as formulas.
            # JSONL retains exact metadata for later import into the app.
            Write-InventoryRecord $Record
        }
        foreach ($Child in $Documents.GetFoldersByParentId($Folder.Id, $false)) {
            if ($null -ne $Child) { $Folders.Push($Child) }
        }
        $FolderCount++
        $Csv.Flush()
        $Json.Flush()
    }
    }
    $Finished = $true
    $Failure = $null
}
catch {
    $Failure = $_.Exception.Message
    throw
}
finally {
    if ($null -ne $Csv) { $Csv.Dispose() }
    if ($null -ne $Json) { $Json.Dispose() }
    $Summary = [ordered]@{
        ScanRoot = $ScanRoot; StartedUtc = $Started; EndedUtc = [DateTime]::UtcNow.ToString('o')
        EnumerationFinished = $Finished; Folders = $FolderCount; Files = $FileCount; Error = $Failure
        ScanMode = $ScanMode; Complete = [bool]$Complete
        Scope = $(if ($ScanMode -eq 'Incremental') { 'Files checked in since the previous scan. Removals are checked on a full scan.' } else { 'Latest file records returned to this user; includes hidden records. No project matching or transfers.' })
    }
    $Summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunFolder 'scan-summary.json') -Encoding UTF8
    Write-Host ('Reports: ' + $RunFolder)
}

Move-Item -LiteralPath $CsvPath -Destination (Join-Path $RunFolder 'inventory.csv')
Move-Item -LiteralPath $JsonPath -Destination (Join-Path $RunFolder 'inventory.jsonl')
Write-Host ('SCAN COMPLETE: ' + $FileCount + ' file records in ' + $FolderCount + ' folders. No files transferred.')
