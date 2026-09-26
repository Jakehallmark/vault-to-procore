param(
    [string]$ClientId,
    [ValidatePattern('^[0-9]+$')][string]$CompanyId = '4290241',
    [string]$OutputFolder = (Join-Path $PSScriptRoot 'reports')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ProcoreSandbox.Core.ps1')
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
try {
    Write-Host ('Developer sandbox only. Company ID: ' + $CompanyId + '. No documents will be changed.')
    if ([string]::IsNullOrWhiteSpace($ClientId)) { $ClientId = Read-Host 'Sandbox Client ID from OAuth Credentials' }
    $ClientId = $ClientId.Trim()
    if (-not $ClientId) { throw 'Sandbox Client ID is required.' }
    $Secret = Read-ProcoreHidden 'Paste the sandbox client secret (input hidden)'
    if ([string]::IsNullOrWhiteSpace($Secret)) { throw 'Sandbox client secret is required.' }
    $Bytes = New-Object byte[] 32
    $Rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $Rng.GetBytes($Bytes) } finally { $Rng.Dispose() }
    $State = [BitConverter]::ToString($Bytes).Replace('-', '')
    $AuthUrl = 'https://login-sandbox.procore.com/oauth/authorize?' + (ConvertTo-ProcoreForm @{
        response_type = 'code'; client_id = $ClientId; redirect_uri = 'http://localhost'; state = $State
    })
    Write-Host 'Sign in and authorize in the browser. The localhost page may say it cannot connect; that is expected.'
    Write-Host 'Copy the COMPLETE localhost address from the browser address bar, then paste it at the hidden prompt below.'
    Write-Host 'Do not paste the callback address into chat. No local web server is needed.'
    try { Start-Process $AuthUrl }
    catch { throw 'Could not open your default browser. Configure a default browser and run again.' }
    $Started = [DateTime]::UtcNow
    $Callback = Read-ProcoreHidden 'Complete localhost callback address (input hidden)'
    if (([DateTime]::UtcNow - $Started).TotalMinutes -gt 10) { throw 'Sign-in timed out. Run again.' }
    $Code = Get-ProcoreAuthorizationCode $Callback.Trim() $State
    $Form = @{ grant_type = 'authorization_code'; client_id = $ClientId; client_secret = $Secret; code = $Code; redirect_uri = 'http://localhost' }
    $TokenResponse = Invoke-ProcoreSandboxRequest -Client $Client -Method POST -Url 'https://login-sandbox.procore.com/oauth/token' -Form $Form
    if ($null -eq $TokenResponse -or $TokenResponse.PSObject.Properties.Name -notcontains 'access_token' -or
        [string]::IsNullOrWhiteSpace($TokenResponse.access_token)) { throw 'Sandbox did not return an access token.' }
    $Token = [string]$TokenResponse.access_token
    $Form.Clear(); $Form = $null; $Secret = $null; $Callback = $null; $Code = $null; $TokenResponse = $null
    $Me = Invoke-ProcoreSandboxRequest -Client $Client -Method GET -Url 'https://sandbox.procore.com/rest/v1.0/me' -Token $Token
    if ($null -eq $Me -or $Me.PSObject.Properties.Name -notcontains 'id') { throw 'Could not verify the signed-in user.' }
    Write-Host 'Sandbox sign-in succeeded. Reading sandbox company projects...'
    $Run = Join-Path $OutputFolder ('procore-sandbox-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($Run)
    $Snapshot = [ordered]@{
        SchemaVersion = 1; Source = 'Procore developer sandbox API'; Environment = 'DeveloperSandbox'
        CompanyId = $CompanyId; UserId = [string]$Me.id; StartedUtc = [DateTime]::UtcNow.ToString('o'); EndedUtc = $null
        Endpoint = ('/rest/v1.0/companies/' + $CompanyId + '/projects'); PerPage = $null
        EnumerationMethod = 'Single company-projects list response; this endpoint documents no pagination parameters.'
        Filters = 'No explicit status filters; endpoint defaults apply.'
        Scope = 'Projects returned to the signed-in user in this sandbox company. Not a production inventory or atomic snapshot. Missing status remains unknown.'
        EnumerationFinished = $false; CompleteCompanyInventoryVerified = $false
        PagesFetched = 0; ProjectCount = 0; Projects = @(); Error = $null
    }
    $FetchProjects = {
        $Url = 'https://sandbox.procore.com/rest/v1.0/companies/' + $CompanyId + '/projects'
        # Unary comma preserves the JSON array through the request function's pipeline.
        $Data = Invoke-ProcoreSandboxRequest -Client $Client -Method GET -Url $Url -Token $Token -CompanyId $CompanyId
        [pscustomobject]@{ Records = $Data }
    }
    Get-ProcoreSandboxProjects -FetchProjects $FetchProjects -CompanyId $CompanyId -Snapshot $Snapshot
}
catch {
    if ($null -ne $Snapshot) { $Snapshot.Error = $_.Exception.Message }
    # Deliberately omit exception bodies and invocation details from authentication failures.
    Write-Host ('Stopped: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
finally {
    $Client.Dispose(); $Handler.Dispose()
    [Net.ServicePointManager]::SecurityProtocol = $PreviousTls
    if ($null -ne $Form) { $Form.Clear() }
    $Secret = $null; $Token = $null; $TokenResponse = $null; $Callback = $null; $Code = $null
    if ($null -ne $Snapshot) {
        $Snapshot.EndedUtc = [DateTime]::UtcNow.ToString('o')
        $Partial = Join-Path $Run 'projects.partial.json'
        [IO.File]::WriteAllText($Partial, ($Snapshot | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
        $Output = $Partial
        if ($Snapshot.EnumerationFinished) {
            $Output = Join-Path $Run 'projects.json'
            [IO.File]::Move($Partial, $Output)
        }
        Write-Host ('Projects retained: ' + $Snapshot.ProjectCount + '. Report: ' + $Output)
    }
}
