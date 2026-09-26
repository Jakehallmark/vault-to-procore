param(
    [string]$ClientId,
    [ValidatePattern('^[0-9]+$')][string]$CompanyId = '12233',
    [string]$OutputFolder = (Join-Path $PSScriptRoot 'reports'),
    [switch]$IncludeDetails,
    [string]$VaultSnapshot,
    [string]$SecretsFile = (Join-Path $PSScriptRoot '.secrets'),
    [string]$TokenFile = (Join-Path $env:LOCALAPPDATA 'VaultTransfer\procore.refresh'),
    [string]$ResumeReport,
    [switch]$ManualCallback,
    [switch]$SkipComparison
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ProcoreSandbox.Core.ps1')
. (Join-Path $PSScriptRoot 'ProcoreProduction.Runtime.ps1')
Add-Type -AssemblyName System.Net.Http
# Explicit TLS 1.2 for Windows PowerShell 5.1; restore the process setting on exit.
$PreviousTls = [Net.ServicePointManager]::SecurityProtocol
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$Handler = New-Object System.Net.Http.HttpClientHandler
$Handler.AllowAutoRedirect = $false
$Client = New-Object System.Net.Http.HttpClient($Handler)
$Client.Timeout = [TimeSpan]::FromSeconds(60)
$Secret = $null; $Token = $null; $TokenResponse = $null; $Callback = $null; $Code = $null; $Form = $null
$Snapshot = $null; $Run = $null
$Session = $null; $InstanceLock = $null
try {
    $RuntimeFolder = Join-Path $PSScriptRoot 'reports'
    [void][IO.Directory]::CreateDirectory($RuntimeFolder)
    try { $InstanceLock = [IO.File]::Open((Join-Path $RuntimeFolder ('production-' + $CompanyId + '.lock')), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another production inventory run is already open for this company. Close it before starting another.' }
    $CooldownPath = Join-Path $RuntimeFolder ('production-' + $CompanyId + '-cooldown.json')
    if (Test-Path -LiteralPath $CooldownPath) {
        $Cooldown = Get-Content -LiteralPath $CooldownPath -Raw | ConvertFrom-Json
        if ($Cooldown.CompanyId -cne $CompanyId) { throw 'Saved cooldown company does not match.' }
        Wait-ProcoreUntil ([DateTimeOffset]::Parse($Cooldown.NextAllowedUtc))
    }
    if ($ResumeReport) { $IncludeDetails = $true }
    if ($IncludeDetails -and -not $SkipComparison) {
        if (-not $VaultSnapshot) {
            $Candidates = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'reports') -Directory -Filter 'project-inventory-*' |
                ForEach-Object { Join-Path $_.FullName 'projects.json' } | Where-Object { Test-Path -LiteralPath $_ })
            if ($Candidates.Count -ne 1) { throw 'Specify -VaultSnapshot with the saved PowerSecure CSV/Vault comparison report.' }
            $VaultSnapshot = $Candidates[0]
        }
        $VaultSource = Get-Content -LiteralPath $VaultSnapshot -Raw | ConvertFrom-Json
        if ($VaultSource.Source -cne 'Portfolio CSV' -or $VaultSource.CompanyName -cne 'PowerSecure, Inc.') {
            throw 'VaultSnapshot must be the saved PowerSecure CSV/Vault comparison.'
        }
        Write-Host ('Vault comparison source: ' + $VaultSnapshot)
    }
    Write-Host ('Production read-only inventory. Company ID: ' + $CompanyId + '. No documents will be changed.')
    if (Test-Path -LiteralPath $SecretsFile -PathType Leaf) {
        $Credentials = Read-ProcoreProductionCredentials $SecretsFile
        if ($ClientId -and $ClientId -cne $Credentials['Client ID']) { $Credentials.Clear(); throw 'Client ID does not match the production credentials file.' }
        $ClientId = $Credentials['Client ID']; $Secret = $Credentials['Client Secret']
        $Credentials.Clear(); $Credentials = $null
        Write-Host 'Loaded production credentials from the local file (values hidden).'
    }
    if ([string]::IsNullOrWhiteSpace($ClientId)) { $ClientId = Read-Host 'Production Client ID from OAuth Credentials' }
    $ClientId = $ClientId.Trim()
    if (-not $ClientId) { throw 'Production Client ID is required.' }
    if (-not $Secret) { $Secret = Read-ProcoreHidden 'Paste the production client secret (input hidden)' }
    if ([string]::IsNullOrWhiteSpace($Secret)) { throw 'Production client secret is required.' }
    $SavedRefresh = Read-ProcoreRefreshToken $TokenFile
    if ($SavedRefresh) {
        Write-Host 'Signing in to Procore with the saved sign-in.'
        $Form = @{ grant_type = 'refresh_token'; client_id = $ClientId; client_secret = $Secret; refresh_token = $SavedRefresh; redirect_uri = 'http://localhost' }
        try {
            $TokenResponse = Invoke-ProcoreRequest -Environment Production -Client $Client -Method POST -Url 'https://login.procore.com/oauth/token' -Form $Form
        }
        catch {
            $Status = $_.Exception.Data['HttpStatus']
            if ($Status -ne 400 -and $Status -ne 401) { throw }
            if (Test-Path -LiteralPath $TokenFile) { Remove-Item -LiteralPath $TokenFile -Force }
            Write-Host 'Saved Procore sign-in was not accepted. A browser window will open.'
            $TokenResponse = $null
        }
        finally { if ($null -ne $Form) { $Form.Clear() }; $Form = $null; $SavedRefresh = $null }
    }
    if ($null -eq $TokenResponse) {
        $Bytes = New-Object byte[] 32
        $Rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $Rng.GetBytes($Bytes) } finally { $Rng.Dispose() }
        $State = [BitConverter]::ToString($Bytes).Replace('-', '')
        $AuthUrl = 'https://login.procore.com/oauth/authorize?' + (ConvertTo-ProcoreForm @{
            response_type = 'code'; client_id = $ClientId; redirect_uri = 'http://localhost'; state = $State
        })
        if ($ManualCallback) {
            Write-Host 'Manual callback mode: authorize, then paste the complete localhost address below. A browser connection error is expected.'
            Start-Process $AuthUrl
            $Started = [DateTime]::UtcNow
            $Callback = Read-ProcoreHidden 'Complete localhost callback address (input hidden)'
            if (([DateTime]::UtcNow - $Started).TotalMinutes -gt 10) { throw 'Sign-in timed out. Run again.' }
            $Code = Get-ProcoreAuthorizationCode $Callback.Trim() $State
        } else {
            Write-Host 'Sign in and authorize in the browser. The localhost callback will be received automatically.'
            $Code = Receive-ProcoreCallback $AuthUrl $State
        }
        $Form = @{ grant_type = 'authorization_code'; client_id = $ClientId; client_secret = $Secret; code = $Code; redirect_uri = 'http://localhost' }
        $TokenResponse = Invoke-ProcoreRequest -Environment Production -Client $Client -Method POST -Url 'https://login.procore.com/oauth/token' -Form $Form
    }
    if ($null -eq $TokenResponse -or $TokenResponse.PSObject.Properties.Name -notcontains 'access_token' -or
        [string]::IsNullOrWhiteSpace($TokenResponse.access_token)) { throw 'Production did not return an access token.' }
    $Token = [string]$TokenResponse.access_token
    $Session = @{Client=$Client; ClientId=$ClientId; Secret=$Secret; Token=$Token; RefreshToken=$null; TokenFile=$TokenFile;
        ExpiresUtc=[DateTimeOffset]::UtcNow; CompanyId=$CompanyId; NextAllowedUtc=[DateTimeOffset]::UtcNow; CooldownPath=$CooldownPath}
    Set-ProcoreSessionTokens $Session $TokenResponse
    if (-not $Session.RefreshToken) { throw 'Procore did not return a refresh token, so the next scan cannot sign in on its own.' }
    if ($null -ne $Form) { $Form.Clear() }; $Form = $null; $Secret = $null; $Callback = $null; $Code = $null; $TokenResponse = $null; $Token = $null
    $Me = Invoke-ProcoreReliableGet $Session 'https://api.procore.com/rest/v1.0/me'
    if ($null -eq $Me -or $Me.PSObject.Properties.Name -notcontains 'id') { throw 'Could not verify the signed-in user.' }
    Write-Host 'Production sign-in succeeded. Reading company projects...'
    $Run = Join-Path $OutputFolder ('procore-production-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($Run)
    $Snapshot = [ordered]@{
        SchemaVersion = 1; Source = 'Procore production API'; Environment = 'Production'
        CompanyId = $CompanyId; UserId = [string]$Me.id; StartedUtc = [DateTime]::UtcNow.ToString('o'); EndedUtc = $null
        Endpoint = ('/rest/v1.0/companies/' + $CompanyId + '/projects'); PerPage = $null
        EnumerationMethod = 'Single company-projects list response; this endpoint documents no pagination parameters.'
        Filters = 'No explicit status filters; endpoint defaults apply.'
        Scope = 'Projects returned to the signed-in user in this production company. Access-limited list, not an atomic snapshot. Missing status remains unknown.'
        EnumerationFinished = $false; CompleteCompanyInventoryVerified = $false
        PagesFetched = 0; ProjectCount = 0; Projects = @(); Error = $null
        DetailsRequested = [bool]$IncludeDetails; DetailsFinished = $false; DetailsFetched = 0
    }
    $FetchProjects = {
        $Url = 'https://api.procore.com/rest/v1.0/companies/' + $CompanyId + '/projects'
        # Unary comma preserves the JSON array through the request function's pipeline.
        $Data = Invoke-ProcoreReliableGet $Session $Url
        [pscustomobject]@{ Records = $Data }
    }
    if ($ResumeReport) {
        $Snapshot = Get-ProcoreResumeSnapshot $ResumeReport $CompanyId ([string]$Me.id)
        Write-Host ('Resuming saved list: ' + $Snapshot.DetailsFetched + ' completed; ' + ($Snapshot.ProjectCount - $Snapshot.DetailsFetched) + ' remaining.')
    }
    else { Get-ProcoreSandboxProjects -FetchProjects $FetchProjects -CompanyId $CompanyId -Snapshot $Snapshot }
    if ($IncludeDetails) {
        $Snapshot.DetailEndpoint = '/rest/v1.0/projects/{id}'
        Save-ProcoreCheckpoint $Snapshot $Run
        foreach ($Project in $Snapshot.Projects) {
            if ($Project.PSObject.Properties.Name -contains 'DetailFetchedUtc' -and $Project.DetailFetchedUtc) { continue }
            $DetailUrl = 'https://api.procore.com/rest/v1.0/projects/' + $Project.ProjectId + '?company_id=' + $CompanyId
            $Detail = Invoke-ProcoreReliableGet $Session $DetailUrl
            Set-ProcoreProjectDetail $Project $Detail $CompanyId
            $Snapshot.DetailsFetched++
            Save-ProcoreCheckpoint $Snapshot $Run
            Write-Host ('Project details: ' + $Snapshot.DetailsFetched + ' / ' + $Snapshot.ProjectCount)
        }
        $Snapshot.DetailsFinished = $true
    }
}
catch {
    if ($null -ne $Snapshot) { $Snapshot.Error = $_.Exception.Message }
    # Deliberately omit exception bodies and invocation details from authentication failures.
    Write-Host ('Stopped: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    if ($null -ne $Session) { $Session.Clear() }
    $Client.Dispose(); $Handler.Dispose()
    [Net.ServicePointManager]::SecurityProtocol = $PreviousTls
    if ($null -ne $Form) { $Form.Clear() }
    $Secret = $null; $Token = $null; $TokenResponse = $null; $Callback = $null; $Code = $null
    if ($null -ne $Snapshot) {
        $Snapshot.EndedUtc = [DateTime]::UtcNow.ToString('o')
        $Partial = Join-Path $Run 'projects.partial.json'
        Save-ProcoreCheckpoint $Snapshot $Run
        $Output = $Partial
        if ($Snapshot.EnumerationFinished -and -not $Snapshot.Error -and
            (-not $Snapshot.DetailsRequested -or $Snapshot.DetailsFinished)) {
            $Output = Join-Path $Run 'projects.json'
            [IO.File]::Move($Partial, $Output)
        }
        Write-Host ('Projects retained: ' + $Snapshot.ProjectCount + '. Report: ' + $Output)
    }
    if ($null -ne $InstanceLock) { $InstanceLock.Dispose() }
}
if ($IncludeDetails -and -not $SkipComparison -and $Snapshot.DetailsFinished -and -not $Snapshot.Error) {
    & (Join-Path $PSScriptRoot 'CompareProcoreMetadata.ps1') -ProcoreReport $Output -VaultSnapshot $VaultSnapshot -OutputFolder $OutputFolder
}
