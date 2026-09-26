param(
    [Parameter(Mandatory=$true)][string]$ProcoreReport,
    [Parameter(Mandatory=$true)][string]$VaultSnapshot,
    [string]$OutputFolder = (Join-Path $PSScriptRoot 'reports')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$Api = Get-Content -LiteralPath $ProcoreReport -Raw | ConvertFrom-Json
$Prior = Get-Content -LiteralPath $VaultSnapshot -Raw | ConvertFrom-Json
if ($Api.Environment -cne 'Production' -or $Api.CompanyId -cne '12233' -or
    -not $Api.EnumerationFinished -or -not $Api.DetailsFinished -or $Api.Error -or
    $Api.DetailsFetched -ne $Api.Projects.Count) { throw 'Use a completed PowerSecure production metadata report.' }
if ($Prior.Source -cne 'Portfolio CSV' -or $Prior.CompanyName -cne 'PowerSecure, Inc.') {
    throw 'Expected the saved PowerSecure CSV/Vault comparison snapshot.'
}
$ByNumber = @{}; $Vault = @{}
$Seen = New-Object 'System.Collections.Generic.HashSet[string]'
$Comparison = New-Object 'System.Collections.Generic.List[object]'
foreach ($Project in $Api.Projects) {
    if ($Project.CompanyId -cne $Api.CompanyId -or $Project.ProjectId -notmatch '^[0-9]+$' -or
        -not $Seen.Add($Project.ProjectId)) { throw 'Wrong company, invalid ID, or duplicate API project.' }
    if ($null -ne $Project.Number -and $Project.Number -isnot [string]) { throw 'Project number must remain text.' }
    if ([string]::IsNullOrWhiteSpace($Project.Number)) {
        $Comparison.Add([pscustomobject]@{ Number=$Project.Number; Status='Missing Procore number'; VaultPaths=@(); ProcoreProjects=@($Project) })
        continue
    }
    # Do not guess numbers from names or silently trim/convert the API's original values.
    if (-not $ByNumber.ContainsKey($Project.Number)) { $ByNumber[$Project.Number] = New-Object 'System.Collections.Generic.List[object]' }
    $ByNumber[$Project.Number].Add($Project)
}
foreach ($Match in $Prior.Matches) {
    foreach ($Path in $Match.VaultPaths) {
        if ($Path -notmatch '^\$/Designs/Projects/\d{6}-\d{6}/(\d{6})(?=\D|$)[^/]*$' -or $Matches[1] -cne $Match.Number) {
            throw 'Saved Vault path does not agree with its project number.'
        }
        if (-not $Vault.ContainsKey($Match.Number)) { $Vault[$Match.Number] = New-Object 'System.Collections.Generic.HashSet[string]' }
        [void]$Vault[$Match.Number].Add($Path)
    }
}
if ($Vault.Count -eq 0) { throw 'No Vault project paths in the supplied snapshot.' }
foreach ($Number in @(@($Vault.Keys) + @($ByNumber.Keys) | Sort-Object -Unique)) {
    $Paths = @(); $Projects = @()
    if ($Vault.ContainsKey($Number)) { $Paths = @($Vault[$Number] | Sort-Object) }
    if ($ByNumber.ContainsKey($Number)) { $Projects = $ByNumber[$Number].ToArray() }
    $Status = 'Needs review: duplicate number'
    if ($Paths.Count -le 1 -and $Projects.Count -le 1) {
        if ($Paths.Count -eq 1 -and $Projects.Count -eq 1) { $Status = 'Exact number match' }
        elseif ($Paths.Count -eq 1) { $Status = 'Vault only in supplied inventories' }
        else { $Status = 'Procore only in supplied inventories' }
    }
    $Comparison.Add([pscustomobject]@{ Number=$Number; Status=$Status; VaultPaths=$Paths; ProcoreProjects=$Projects })
}
$RetainedIds = @($Comparison | ForEach-Object { $_.ProcoreProjects } | ForEach-Object { $_.ProjectId })
if ($RetainedIds.Count -ne $Api.Projects.Count -or @($RetainedIds | Sort-Object -Unique).Count -ne $Api.Projects.Count) {
    throw 'Comparison did not preserve every API project exactly once.'
}
$Report = [ordered]@{
    SchemaVersion=1; ImportedUtc=[DateTime]::UtcNow.ToString('o'); CompanyId=$Api.CompanyId
    ProcoreSHA256=(Get-FileHash -LiteralPath $ProcoreReport -Algorithm SHA256).Hash
    VaultSnapshotSHA256=(Get-FileHash -LiteralPath $VaultSnapshot -Algorithm SHA256).Hash
    OriginalVaultSHA256=$Prior.VaultSHA256; VaultSnapshotImportedUtc=$Prior.ImportedUtc
    CompleteCompanyInventoryVerified=$false
    Scope='Exact original project numbers. Vault paths reused from the historical CSV/Vault snapshot, not a fresh scan. Empty projects and other Vault branches may be absent. API permission scope is not company-wide completeness. Matches do not approve transfers.'
    ProjectCount=$Api.Projects.Count; PreviousCsvProjectCount=$Prior.ProjectCount
    MatchCounts=@($Comparison | Group-Object Status | Select-Object Name,Count)
    Matches=$Comparison.ToArray()
}
$Json = $Report | ConvertTo-Json -Depth 12
$Run = Join-Path $OutputFolder ('production-vault-comparison-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($Run)
$Temp = Join-Path $Run 'comparison.partial.json'
$Target = Join-Path $Run 'comparison.json'
[IO.File]::WriteAllText($Temp,$Json,(New-Object Text.UTF8Encoding($false)))
[IO.File]::Move($Temp,$Target)
$Report.MatchCounts | Format-Table -AutoSize | Out-Host
Write-Host ('Comparison saved: ' + $Target)
