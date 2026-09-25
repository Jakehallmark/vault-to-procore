param(
    [Parameter(Mandatory = $true)][string]$ProcoreCsv,
    [Parameter(Mandatory = $true)][string]$VaultInventory,
    [Parameter(Mandatory = $true)][string]$CompanyName,
    [string]$OutputFolder = (Join-Path $PSScriptRoot 'reports')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$InputStream = [IO.File]::Open((Resolve-Path -LiteralPath $ProcoreCsv).Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
$Buffer = New-Object IO.MemoryStream
try { $InputStream.CopyTo($Buffer); $CsvBytes = $Buffer.ToArray() }
finally { $InputStream.Dispose(); $Buffer.Dispose() }
$Hasher = [Security.Cryptography.SHA256]::Create()
try { $CsvHash = [BitConverter]::ToString($Hasher.ComputeHash($CsvBytes)).Replace('-', '') }
finally { $Hasher.Dispose() }
$CsvText = [Text.Encoding]::UTF8.GetString($CsvBytes).TrimStart([char]0xFEFF)
$Projects = @($CsvText | ConvertFrom-Csv)
if ($Projects.Count -eq 0) { throw 'The Procore export is empty; no snapshot created.' }
foreach ($Column in @('Project Name', 'Number', 'Stage')) {
    if ($Projects[0].PSObject.Properties.Name -notcontains $Column) { throw ('Missing CSV column: ' + $Column) }
}
if ([string]::IsNullOrWhiteSpace($CompanyName)) { throw 'Company name is required.' }
$Procore = @{}
$Vault = @{}
$Imported = New-Object 'System.Collections.Generic.List[object]'
$Row = 1
foreach ($Project in $Projects) {
    $Row++
    if ([string]::IsNullOrWhiteSpace($Project.'Project Name')) { throw ('Missing project name at CSV record ' + $Row) }
    $Number = ([string]$Project.Number).Trim()
    $Entry = [pscustomobject]@{
        SourceRow = $Row; CompanyName = $CompanyName; CompanyId = $null; ProjectId = $null
        Number = $Number; Name = $Project.'Project Name'; Stage = $Project.Stage
        Active = $null; SourceRecord = $Project
    }
    $Imported.Add($Entry)
    if ($Number) {
        if (-not $Procore.ContainsKey($Number)) { $Procore[$Number] = New-Object 'System.Collections.Generic.List[object]' }
        $Procore[$Number].Add($Entry)
    }
}
$Reader = [IO.File]::OpenText((Resolve-Path -LiteralPath $VaultInventory).Path)
try {
    while ($null -ne ($Line = $Reader.ReadLine())) {
        if ([string]::IsNullOrWhiteSpace($Line)) { continue }
        $File = $Line | ConvertFrom-Json
        if ($File.VaultPath -match '^(\$/Designs/Projects/\d{6}-\d{6}/(\d{6})(?=\D|$)[^/]+)/') {
            $Root = $Matches[1]; $Number = $Matches[2]
            if (-not $Vault.ContainsKey($Number)) { $Vault[$Number] = New-Object 'System.Collections.Generic.HashSet[string]' }
            [void]$Vault[$Number].Add($Root)
        }
    }
}
finally { $Reader.Dispose() }
if ($Vault.Count -eq 0) { throw 'No numbered project folders found in the Vault inventory.' }
$Comparison = New-Object 'System.Collections.Generic.List[object]'
$Numbers = @(@($Vault.Keys) + @($Procore.Keys) | Sort-Object -Unique)
foreach ($Number in $Numbers) {
    $Paths = @(); $Entries = @()
    if ($Vault.ContainsKey($Number)) { $Paths = @($Vault[$Number] | Sort-Object) }
    if ($Procore.ContainsKey($Number)) { $Entries = @($Procore[$Number].ToArray()) }
    $Status = 'Needs review'
    if ($Paths.Count -le 1 -and $Entries.Count -le 1) {
        if ($Paths.Count -eq 1 -and $Entries.Count -eq 1) { $Status = 'Exact number match' }
        elseif ($Paths.Count -eq 1) { $Status = 'Vault only in supplied inventories' }
        else { $Status = 'Procore only in supplied inventories' }
    }
    $Comparison.Add([pscustomobject]@{
        Number = $Number; Status = $Status; VaultPaths = $Paths
        ProcoreProjects = @($Entries | Select-Object SourceRow, Number, Name, Stage)
    })
}
foreach ($Entry in $Imported) {
    if (-not $Entry.Number) {
        $Comparison.Add([pscustomobject]@{
            Number = ''; Status = 'Missing Procore number'; VaultPaths = @()
            ProcoreProjects = @($Entry | Select-Object SourceRow, Number, Name, Stage)
        })
    }
}
$Snapshot = [ordered]@{
    SchemaVersion = 1; ImportedUtc = [DateTime]::UtcNow.ToString('o'); CompanyName = $CompanyName
    Source = 'Portfolio CSV'; CsvSHA256 = $CsvHash
    VaultSHA256 = (Get-FileHash -LiteralPath $VaultInventory -Algorithm SHA256).Hash
    CompleteCompanyInventoryVerified = $false
    Scope = 'CSV filters and active/inactive scope are unknown. Vault projects are inferred from file records in numbered range folders; empty projects and other branches are not covered.'
    ProjectCount = $Imported.Count; Projects = $Imported.ToArray()
    MatchCounts = @($Comparison | Group-Object Status | Select-Object Name, Count)
    Matches = $Comparison.ToArray()
}
# Serialize before publishing a new immutable snapshot; previous snapshots remain intact.
$Json = $Snapshot | ConvertTo-Json -Depth 10
$Run = Join-Path $OutputFolder ('project-inventory-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($Run)
$Temporary = Join-Path $Run 'projects.partial.json'
$Target = Join-Path $Run 'projects.json'
[IO.File]::WriteAllText($Temporary, $Json, (New-Object Text.UTF8Encoding($false)))
[IO.File]::Move($Temporary, $Target)
$Snapshot.MatchCounts | Format-Table -AutoSize | Out-Host
Write-Host ('Imported Procore projects: ' + $Imported.Count)
Write-Host ('Offline inventory and matching report: ' + $Target)

