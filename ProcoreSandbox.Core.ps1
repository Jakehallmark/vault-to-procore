# Shared, side-effect-free helpers for the sandbox connection check and offline tests.
function Read-ProcoreProductionCredentials([string]$Path) {
    $Values = @{}; $Production = $false
    try { $Lines = [IO.File]::ReadAllLines($Path) }
    catch { throw 'Could not read the local credentials file.' }
    foreach ($Line in $Lines) {
        $Text = $Line.Trim()
        if (-not $Text) { continue }
        if ($Text -match '^#\s*(Production|Sandbox)\s*$') { $Production = $Matches[1] -ieq 'Production'; continue }
        if ($Text.StartsWith('#')) { continue }
        if (-not $Production) { continue }
        if ($Text -notmatch '^(Client ID|Client Secret)\s*:\s*(.+)$') { throw 'Invalid production credential entry. Expected Client ID: or Client Secret:.' }
        $Key = $Matches[1]; $Value = $Matches[2].Trim()
        if ($Values.ContainsKey($Key)) { throw 'Duplicate production credential entry.' }
        $Values[$Key] = $Value
    }
    if (-not $Values['Client ID'] -or -not $Values['Client Secret']) { throw 'The #Production section must contain Client ID: and Client Secret:.' }
    return $Values
}

function ConvertTo-ProcoreForm([hashtable]$Fields) {
    ($Fields.GetEnumerator() | Sort-Object Key | ForEach-Object {
        [Uri]::EscapeDataString([string]$_.Key) + '=' + [Uri]::EscapeDataString([string]$_.Value)
    }) -join '&'
}

function Get-ProcoreAuthorizationCode([string]$Callback, [string]$ExpectedState) {
    $Uri = $null
    if (-not [Uri]::TryCreate($Callback, [UriKind]::Absolute, [ref]$Uri) -or
        $Uri.Scheme -cne 'http' -or $Uri.Host -cne 'localhost' -or $Uri.Port -ne 80 -or
        $Uri.AbsolutePath -cne '/' -or $Uri.UserInfo -or $Uri.Fragment) {
        throw 'Paste the complete http://localhost callback address from this sign-in attempt.'
    }
    $Query = @{}
    foreach ($Pair in $Uri.Query.TrimStart('?').Split('&')) {
        $Parts = $Pair.Split('=', 2)
        if ($Parts.Count -ne 2) { throw 'The callback query is invalid.' }
        $Key = [Uri]::UnescapeDataString($Parts[0].Replace('+', ' '))
        if ($Query.ContainsKey($Key)) { throw 'Duplicate callback parameter; start sign-in again.' }
        $Query[$Key] = [Uri]::UnescapeDataString($Parts[1].Replace('+', ' '))
    }
    if (-not $ExpectedState -or $Query['state'] -cne $ExpectedState) {
        throw 'Sign-in state did not match. Start again and use the newly opened browser tab.'
    }
    if ($Query.ContainsKey('error')) { throw 'Procore authorization was declined or failed. No project query was made.' }
    if ([string]::IsNullOrWhiteSpace($Query['code'])) { throw 'The callback contains no authorization code.' }
    return $Query['code']
}

function Read-ProcoreHidden([string]$Prompt) {
    $Secure = Read-Host $Prompt -AsSecureString
    $Pointer = [IntPtr]::Zero
    try {
        $Pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($Pointer)
    }
    finally {
        if ($Pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($Pointer) }
        $Secure.Dispose()
    }
}

function Invoke-ProcoreRequest {
    param($Client, [ValidateSet('GET','POST')][string]$Method, [string]$Url,
        [string]$Token, [string]$CompanyId, [hashtable]$Form, [ValidateSet('Sandbox','Production')][string]$Environment = 'Sandbox',
        [hashtable]$ResponseMetadata)
    $AuthHost = if ($Environment -eq 'Production') { 'login.procore.com' } else { 'login-sandbox.procore.com' }
    $ApiHost = if ($Environment -eq 'Production') { 'api.procore.com' } else { 'sandbox.procore.com' }
    $Uri = [Uri]$Url
    $Allowed = ($Method -eq 'POST' -and $Uri.Host -ceq $AuthHost -and
        $Uri.AbsolutePath -ceq '/oauth/token' -and -not $Uri.Query) -or
        ($Method -eq 'GET' -and $Uri.Host -ceq $ApiHost -and
        $Uri.AbsolutePath -match '^/rest/v1\.0/(me|companies/[0-9]+/projects)$')
    if ($Environment -eq 'Production' -and $Method -eq 'GET' -and $Uri.Host -ceq $ApiHost -and
        $Uri.AbsolutePath -match '^/rest/v1\.0/projects/[0-9]+$' -and $CompanyId -match '^[0-9]+$' -and
        $Uri.Query -ceq ('?company_id=' + $CompanyId)) { $Allowed = $true }
    if (-not $Allowed -or $Uri.Scheme -cne 'https' -or $Uri.Port -ne 443 -or $Uri.UserInfo -or $Uri.Fragment) {
        throw 'Request blocked: only the selected environment and read-only inventory are supported.'
    }
    if ($Environment -eq 'Production' -and $Uri.AbsolutePath -match '^/rest/v1\.0/companies/([0-9]+)/projects$' -and
        $CompanyId -cne $Matches[1]) {
        throw 'Company header must match the requested production company.'
    }
    $Request = New-Object System.Net.Http.HttpRequestMessage
    $Response = $null
    try {
        $Request.Method = New-Object System.Net.Http.HttpMethod($Method)
        $Request.RequestUri = $Uri
        $Request.Headers.Accept.ParseAdd('application/json')
        if ($Token) { $Request.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $Token) }
        if ($CompanyId) { $Request.Headers.Add('Procore-Company-Id', $CompanyId) }
        if ($Form) {
            $Request.Content = New-Object System.Net.Http.StringContent((ConvertTo-ProcoreForm $Form), [Text.Encoding]::UTF8, 'application/x-www-form-urlencoded')
        }
        try { $Response = $Client.SendAsync($Request).GetAwaiter().GetResult() }
        catch {
            $Failure = [Exception]::new('Procore request failed or timed out. Request details were withheld.')
            $Failure.Data['TransientNetwork'] = $true
            throw $Failure
        }
        $Status = [int]$Response.StatusCode
        if ($null -ne $ResponseMetadata) {
            $ResponseMetadata.Clear()
            $ResponseMetadata['Status'] = $Status
            foreach ($Header in @('Retry-After','X-Rate-Limit-Reset','X-Rate-Limit-Remaining','Date')) {
                if ($Response.Headers.Contains($Header)) { $ResponseMetadata[$Header] = @($Response.Headers.GetValues($Header))[0] }
            }
        }
        if (-not $Response.IsSuccessStatusCode) {
            $Advice = switch ($Status) {
                401 { 'Check credentials for the selected environment and sign in again.' }
                403 { 'Check app installation and user permissions in this company.' }
                429 {
                    $Wait = 'Rate limited. Wait before resuming.'
                    if ($Response.Headers.Contains('Retry-After')) { $Wait += ' Retry-After: ' + (@($Response.Headers.GetValues('Retry-After')) -join ',') + '.' }
                    if ($Response.Headers.Contains('X-Rate-Limit-Reset')) { $Wait += ' Reset: ' + (@($Response.Headers.GetValues('X-Rate-Limit-Reset')) -join ',') + ' (Unix time).'
                    }
                    $Wait
                }
                default { 'Check the app setup and Procore availability before retrying.' }
            }
            $Failure = [Exception]::new('Procore HTTP ' + $Status + '. ' + $Advice + ' Response body withheld to protect credentials.')
            $Failure.Data['HttpStatus'] = $Status
            throw $Failure
        }
        try {
            $Json = $Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $Parsed = $Json | ConvertFrom-Json
            if ($Json.TrimStart().StartsWith('[')) { return ,@($Parsed) }
            return $Parsed
        }
        catch { throw 'Procore returned an unreadable JSON response. Response body withheld.' }
    }
    finally {
        if ($null -ne $Response) { $Response.Dispose() }
        $Request.Dispose()
    }
}

function Set-ProcoreProjectDetail {
    param($Project, $Detail, [string]$CompanyId)
    if ($null -eq $Detail -or $Detail -is [Array] -or $Detail.PSObject.Properties.Name -notcontains 'id' -or
        [string]$Detail.id -cne [string]$Project.ProjectId) { throw 'Project detail ID did not match the requested project.' }
    if ($Detail.PSObject.Properties.Name -contains 'company_id' -and [string]$Detail.company_id -cne $CompanyId) {
        throw 'Project detail company did not match the requested company.'
    }
    $Number = $null; $Active = $null
    if ($Detail.PSObject.Properties.Name -contains 'project_number') { $Number = $Detail.project_number }
    if ($null -ne $Number -and $Number -isnot [string]) { throw 'Detailed project number was not text.' }
    if ($Detail.PSObject.Properties.Name -contains 'active') { $Active = $Detail.active }
    if ($null -ne $Active -and $Active -isnot [bool]) { throw 'Detailed active status was not boolean.' }
    # Validate the complete record before updating the retained entry.
    $Project.Number = $Number; $Project.Active = $Active
    if ($Detail.PSObject.Properties.Name -contains 'name') { $Project.Name = [string]$Detail.name }
    $Project | Add-Member NoteProperty DetailFetchedUtc ([DateTime]::UtcNow.ToString('o')) -Force
}

function Save-ProcoreCheckpoint {
    param($Snapshot, [string]$Run)
    $Target = Join-Path $Run 'projects.partial.json'
    $Temp = Join-Path $Run 'checkpoint.tmp'
    [IO.File]::WriteAllText($Temp, ($Snapshot | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    # PowerShell 7 / .NET supports overwrite move; previous complete reports are never touched.
    [IO.File]::Move($Temp, $Target, $true)
}

function Get-ProcoreResumeSnapshot {
    param([string]$Path, [string]$CompanyId, [string]$UserId)
    $Prior = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($Prior.Environment -cne 'Production' -or $Prior.CompanyId -cne $CompanyId -or
        $Prior.UserId -cne $UserId -or -not $Prior.EnumerationFinished -or -not $Prior.DetailsRequested) {
        throw 'Resume report must contain an enumerated production list for the same company and signed-in user.'
    }
    $Seen = New-Object 'System.Collections.Generic.HashSet[string]'
    $Done = 0
    foreach ($Project in $Prior.Projects) {
        if ($Project.CompanyId -cne $CompanyId -or $Project.ProjectId -notmatch '^[0-9]+$' -or -not $Seen.Add($Project.ProjectId)) {
            throw 'Resume report has invalid, duplicate, or cross-company project IDs.'
        }
        if ($Project.PSObject.Properties.Name -contains 'DetailFetchedUtc' -and $Project.DetailFetchedUtc) {
            $ParsedDate = [DateTime]::MinValue
            if (-not [DateTime]::TryParse($Project.DetailFetchedUtc, [ref]$ParsedDate) -or
                ($null -ne $Project.Number -and $Project.Number -isnot [string]) -or
                ($null -ne $Project.Active -and $Project.Active -isnot [bool])) { throw 'Invalid completed metadata in resume report.' }
            $Done++
        }
    }
    if ($Prior.ProjectCount -ne $Seen.Count -or $Prior.DetailsFetched -ne $Done) { throw 'Resume report counts do not agree with retained records.' }
    $Snapshot = [ordered]@{}
    foreach ($Property in $Prior.PSObject.Properties) { $Snapshot[$Property.Name] = $Property.Value }
    $Snapshot.Error = $null; $Snapshot.EndedUtc = $null; $Snapshot.DetailsFinished = $false
    $Snapshot.ResumeSourceSHA256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $Snapshot.ResumedUtc = [DateTime]::UtcNow.ToString('o')
    return $Snapshot
}

function Get-ProcoreSandboxProjects {
    param([scriptblock]$FetchProjects, [string]$CompanyId, $Snapshot)
    $Seen = New-Object 'System.Collections.Generic.HashSet[string]'
    $Rows = New-Object 'System.Collections.Generic.List[object]'
    # Company Projects documents a single list response, with no page/per_page parameters.
    # Do not manufacture page requests: this endpoint may simply ignore them.
    $Result = & $FetchProjects
    $Records = $Result.Records
    if ($null -eq $Records -or $Records -isnot [Array]) { throw 'Project endpoint did not return a JSON array.' }
    $Snapshot.PagesFetched = 1
    if ($Records.Count -eq 0) { $Snapshot.EnumerationFinished = $true; return }
    foreach ($Record in $Records) {
        if ($null -eq $Record -or $Record.PSObject.Properties.Name -notcontains 'id' -or
            [string]$Record.id -notmatch '^[0-9]+$') { throw 'Project response is missing a valid ID.' }
        $Id = [string]$Record.id
        if (-not $Seen.Add($Id)) { throw 'Repeated project ID in the company project response.' }
        $Number = $null; $Active = $null; $Name = $null
        if ($Record.PSObject.Properties.Name -contains 'project_number') { $Number = $Record.project_number }
        if ($null -ne $Number -and $Number -isnot [string]) { throw 'Project number was not text; leading zeros cannot be verified.' }
        if ($Record.PSObject.Properties.Name -contains 'name') { $Name = [string]$Record.name }
        if ($Record.PSObject.Properties.Name -contains 'active') {
            if ($null -ne $Record.active -and $Record.active -isnot [bool]) { throw 'Unexpected active status value.' }
            $Active = $Record.active
        }
        $Rows.Add([pscustomobject]@{ CompanyId = $CompanyId; ProjectId = $Id; Number = $Number; Name = $Name; Active = $Active })
    }
    $Snapshot.Projects = $Rows.ToArray()
    $Snapshot.ProjectCount = $Rows.Count
    $Snapshot.EnumerationFinished = $true
}

# Compatibility wrapper keeps the verified sandbox entry point sandbox-only.
function Invoke-ProcoreSandboxRequest {
    param($Client, [ValidateSet('GET','POST')][string]$Method, [string]$Url,
        [string]$Token, [string]$CompanyId, [hashtable]$Form)
    Invoke-ProcoreRequest @PSBoundParameters -Environment Sandbox
}
