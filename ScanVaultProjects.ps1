param(
    [Parameter(Mandatory = $true)]$Connection,
    [string]$ScanRoot = '$/Designs/Projects',
    [string]$ReportFolder = (Join-Path $PSScriptRoot 'reports')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if ($null -eq $Connection) { throw 'An Active Vault connection is required' }
if (-not $ScanRoot.StartsWith('$/')) { throw 'Use a vault path beginning with $/.' }

$Documents = $Connection.WebServiceManager.DocumentService
$StartFolder = $Documents.GetFolderByPath($ScanRoot)
if ($null -eq $StartFolder) { throw ('Vault folder was not found: ' + $ScanRoot) }

$RunFolder = Join-Path $ReportFolder ((Get-Date -Format 'yyyyMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($RunFolder)
$CsvPath = Join-Path $RunFolder 'inventory.partial.csv'
$JsonPath = Join-Path $RunFolder 'inventory.partial.json1'
$Csv = $null
$Json = $null
$Folders = New-Object 'System.Collections.Generic.Stack[object]'
$Seen = New-Object 'System.Collections.Generic.HashSet[long]'
$Folders.Push($StartFolder)
$FileCount = 0
$FolderCount = 0
$Finished = $false
$Failure = 'Interupted before completion.'
$Started = [DateTime]::UtcNow.ToString('o')

try {
    $Csv = New-Object IO.StreamWriter($CsvPath, $false, [Text.Encoding]::UTF8)
    $Json = New-Object IO.StreamWriter($JsonPath, $false, [Text.Encoding]::UTF8)
    $Csv.WriteLine('VaultPath,FileName,FileId,MasterId,Version,Decision')
    while ($Folders.Count -gt 0) {
        $Folder = $Folders.Pop()
        if (-not $Seen.Add([long]$Folder.Id)) { throw 'A folder was returned twice; stopped for review' }
        Write-Host ('Scanning: ' + $Folder.FullName)
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
            $Json.WriteLine(($Record | ConvertTo-Json -Compress))
            $Cells = foreach ($Value in @($Record.VaultPath, $Record.FileName, $Record.FileId, $Record.MasterId, $Record.Version, $Record.Decision)) {
                '"' + ('"' + [string]$value).Replace('"', '"') + '"'
            }
            $Csv.WriteLine(($Cells -join ','))
            $FileCount++
        }
        foreach ($Child in $Documents.GetFoldersByParentId($Folder.Id, $false)) {
            if ($null -ne $Child) { $Folders.Push($Child) }
        }
        $FolderCount++
        $Csv.Flush()
        $Json.Flush()
    }
    $Finished = $true
    $Failure  = $null
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
        Scope = 'All Counts'
    }
    $Summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunFolder 'scan-summary.json') -Encoding UTF8
    Write-Host ('Reports: ' + $RunFolder)
}

Move-Item -LiteralPath $CsvPath -Destination (Join-Path $RunFolder 'inventory.csv')
Move-Item -LiteralPath $JsonPath -Destination (Join-Path $RunFolder 'inventory.json1')
Write-Host ('Scan Complete: ' + $FileCount + ' file records in ' + $FolderCount + ' folders.')