# Offline tests only. No browser, credentials, or network calls.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ProcoreSandbox.Core.ps1')
Add-Type -AssemblyName System.Net.Http
$Passed = 0
function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:Passed++
}
function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $Rejected = $false
    try { & $Action | Out-Null } catch { $Rejected = $true }
    Assert $Rejected $Message
}
Assert ((Get-ProcoreAuthorizationCode 'http://localhost/?code=a%2Bb&state=ABC' 'ABC') -ceq 'a+b') 'Callback decoding failed.'
foreach ($Bad in @(
    'http://localhost/?code=a&state=abc',
    'https://example.com/?code=a&state=ABC',
    'http://localhost:9999/?code=a&state=ABC',
    'http://localhost/other?code=a&state=ABC',
    'http://localhost/?code=a&state=ABC&state=ABC',
    'http://localhost/?error=access_denied&state=ABC',
    'http://localhost/?state=ABC',
    'http://localhost/?code=a&state=ABC#fragment'
)) { Assert-Rejected { Get-ProcoreAuthorizationCode $Bad 'ABC' } 'Unsafe callback accepted.' }
Assert ((ConvertTo-ProcoreForm @{ code = 'a+b&c' }) -ceq 'code=a%2Bb%26c') 'Form encoding failed.'

# A fake HttpClient returns real HttpResponseMessage objects without sending anything.
$Fake = [pscustomobject]@{ Body = '[]'; Status = 200; Calls = 0; LastMethod=''; LastBody=''; LastAuth=''; LastCompany='' }
$Fake | Add-Member ScriptMethod SendAsync {
    param($Request)
    $this.Calls++
    $this.LastMethod = $Request.Method.Method
    $this.LastBody = if ($null -ne $Request.Content) { $Request.Content.ReadAsStringAsync().GetAwaiter().GetResult() } else { '' }
    $this.LastAuth = [string]$Request.Headers.Authorization
    $this.LastCompany = if ($Request.Headers.Contains('Procore-Company-Id')) { @($Request.Headers.GetValues('Procore-Company-Id'))[0] } else { '' }
    $Response = New-Object System.Net.Http.HttpResponseMessage([Net.HttpStatusCode]$this.Status)
    $Response.Content = New-Object System.Net.Http.StringContent($this.Body)
    $Completion = New-Object 'System.Threading.Tasks.TaskCompletionSource[System.Net.Http.HttpResponseMessage]'
    $Completion.SetResult($Response)
    return $Completion.Task
}
$Url = 'https://sandbox.procore.com/rest/v1.0/companies/123/projects'
$Empty = Invoke-ProcoreSandboxRequest -Client $Fake -Method GET -Url $Url
Assert ($Empty -is [Array] -and $Empty.Count -eq 0) 'Empty array was lost.'
$Fake.Body = '[{"id":1,"project_number":"001234","active":false}]'
$Single = Invoke-ProcoreSandboxRequest -Client $Fake -Method GET -Url $Url
Assert ($Single -is [Array] -and $Single.Count -eq 1) 'Single-record array was lost.'
Invoke-ProcoreSandboxRequest -Client $Fake -Method GET -Url $Url -Token 'offline-test-token' -CompanyId '123' | Out-Null
Assert ($Fake.LastAuth -ceq 'Bearer offline-test-token' -and $Fake.LastCompany -ceq '123' -and $Fake.LastMethod -ceq 'GET') 'Authorization or company headers missing.'
$Fake.Body = '{"access_token":"offline-test-token"}'
$TokenResult = Invoke-ProcoreSandboxRequest -Client $Fake -Method POST -Url 'https://login-sandbox.procore.com/oauth/token' -Form @{client_secret='fake+a&b'; code='test'}
Assert ($TokenResult.access_token -ceq 'offline-test-token' -and $Fake.LastMethod -ceq 'POST' -and $Fake.LastBody -ceq 'client_secret=fake%2Ba%26b&code=test') 'Token exchange form failed.'
Assert-Rejected { Invoke-ProcoreSandboxRequest -Client $Fake -Method GET -Url 'https://api.procore.com/rest/v1.0/me' } 'Production host accepted.'
Assert-Rejected { Invoke-ProcoreSandboxRequest -Client $Fake -Method POST -Url $Url } 'Project write accepted.'
foreach ($Status in @(302, 401, 403, 429, 500)) {
    $Fake.Status = $Status; $Fake.Body = 'sensitive-body-marker'
    $Message = ''
    try { Invoke-ProcoreSandboxRequest -Client $Fake -Method GET -Url $Url | Out-Null }
    catch { $Message = $_.Exception.Message }
    Assert ($Message -like ('*HTTP ' + $Status + '*') -and $Message -notlike '*sensitive-body-marker*') 'HTTP error was not safely reported.'
}
$Fake.Status = 200; $Fake.Body = 'not-json-sensitive-body-marker'
Assert-Rejected { Invoke-ProcoreSandboxRequest -Client $Fake -Method GET -Url $Url } 'Invalid JSON accepted.'

function New-Snapshot { [ordered]@{ PagesFetched=0; EnumerationFinished=$false; Projects=@(); ProjectCount=0 } }
$Snapshot = New-Snapshot
$script:FetchCalls = 0
$Fetch = {
    $script:FetchCalls++
    # Reproduce a server returning the same two projects on every request.
    [pscustomobject]@{ Records = @(
        [pscustomobject]@{ id=1; name='First'; project_number='001234'; active=$false },
        [pscustomobject]@{ id=2; name='Second' }
    ) }
}
Get-ProcoreSandboxProjects $Fetch '123' $Snapshot
Assert ($Snapshot.EnumerationFinished -and $Snapshot.PagesFetched -eq 1 -and $Snapshot.ProjectCount -eq 2 -and $script:FetchCalls -eq 1) 'Company list was queried more than once.'
Assert ($Snapshot.Projects[0].Number -ceq '001234' -and $Snapshot.Projects[0].Active -eq $false) 'Number or inactive status changed.'
Assert ($null -eq $Snapshot.Projects[1].Number -and $null -eq $Snapshot.Projects[1].Active) 'Missing values were inferred.'
$Snapshot = New-Snapshot
Assert-Rejected { Get-ProcoreSandboxProjects { [pscustomobject]@{ Records=@([pscustomobject]@{id=1}, [pscustomobject]@{id=1}) } } '123' $Snapshot } 'Duplicate IDs accepted.'
Assert (-not $Snapshot.EnumerationFinished) 'Duplicate failure marked complete.'
$Snapshot = New-Snapshot
Assert-Rejected { Get-ProcoreSandboxProjects { throw 'HTTP 403' } '123' $Snapshot } 'Query failure accepted.'
Assert (-not $Snapshot.EnumerationFinished -and $Snapshot.ProjectCount -eq 0) 'Query failure marked complete.'
$Snapshot = New-Snapshot
Get-ProcoreSandboxProjects { param($Page) [pscustomobject]@{Records=@()} } '123' $Snapshot
Assert ($Snapshot.EnumerationFinished -and $Snapshot.ProjectCount -eq 0) 'Empty inventory failed.'
foreach ($Records in @(
    [pscustomobject]@{id=1},
    @([pscustomobject]@{name='Missing ID'}),
    @([pscustomobject]@{id=1;project_number=1234}),
    @([pscustomobject]@{id=1;active='false'})
)) {
    $Snapshot = New-Snapshot
    Assert-Rejected { Get-ProcoreSandboxProjects { param($Page) [pscustomobject]@{Records=$Records} } '123' $Snapshot } 'Malformed response accepted.'
}
$Fake.Status = 200; $Fake.Body = '[{"id":42,"name":"Production test record"}]'
$ProductionUrl = 'https://api.procore.com/rest/v1.0/companies/12233/projects'
$ProductionRows = Invoke-ProcoreRequest -Client $Fake -Environment Production -Method GET -Url $ProductionUrl -CompanyId '12233' -Token 'offline-production-token'
Assert ($ProductionRows.Count -eq 1 -and $Fake.LastCompany -ceq '12233' -and $Fake.LastAuth -ceq 'Bearer offline-production-token') 'Production company query failed.'
$Fake.Body = '{"access_token":"offline-production-token"}'
$ProductionToken = Invoke-ProcoreRequest -Client $Fake -Environment Production -Method POST -Url 'https://login.procore.com/oauth/token' -Form @{ code='test'; client_secret='fake' }
Assert ($ProductionToken.access_token -ceq 'offline-production-token') 'Production token exchange failed.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method GET -Url 'https://sandbox.procore.com/rest/v1.0/me' } 'Production helper accepted sandbox API.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method POST -Url 'https://login-sandbox.procore.com/oauth/token' } 'Production helper accepted sandbox authentication.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method GET -Url $ProductionUrl -CompanyId '4290241' } 'Mismatched production company accepted.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method POST -Url $ProductionUrl -CompanyId '12233' } 'Production project write accepted.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method GET -Url 'https://api.procore.com/rest/v1.0/projects/42/files' } 'Document endpoint accepted.'
$Fake.Body = '{"id":42,"project_number":"001234","active":false,"company_id":12233}'
$Detail = Invoke-ProcoreRequest -Client $Fake -Environment Production -Method GET -Url 'https://api.procore.com/rest/v1.0/projects/42?company_id=12233' -CompanyId '12233'
$Project = [pscustomobject]@{ ProjectId='42'; Number=$null; Active=$null; Name='Original' }
Set-ProcoreProjectDetail $Project $Detail '12233'
Assert ($Project.Number -ceq '001234' -and $Project.Active -eq $false -and $Project.DetailFetchedUtc) 'Detailed values were not preserved.'
Assert-Rejected { Set-ProcoreProjectDetail $Project ([pscustomobject]@{id=43}) '12233' } 'Wrong detail ID accepted.'
Assert-Rejected { Set-ProcoreProjectDetail $Project ([pscustomobject]@{id=42;company_id=99}) '12233' } 'Wrong detail company accepted.'
Assert-Rejected { Set-ProcoreProjectDetail $Project ([pscustomobject]@{id=42;project_number=1234}) '12233' } 'Numeric detail number accepted.'
Assert-Rejected { Set-ProcoreProjectDetail $Project ([pscustomobject]@{id=42;active='false'}) '12233' } 'String detail active value accepted.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method GET -Url 'https://api.procore.com/rest/v1.0/projects/42' } 'Detail endpoint accepted without company header.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method POST -Url 'https://api.procore.com/rest/v1.0/projects/42?company_id=12233' -CompanyId '12233' } 'Project detail write accepted.'
Assert-Rejected { Invoke-ProcoreSandboxRequest -Client $Fake -Method GET -Url 'https://sandbox.procore.com/rest/v1.0/projects/42' -CompanyId '12233' } 'Sandbox scope expanded inadvertently.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method GET -Url 'https://api.procore.com/rest/v1.0/projects/42' -CompanyId '12233' } 'Missing company query accepted.'
Assert-Rejected { Invoke-ProcoreRequest -Client $Fake -Environment Production -Method GET -Url 'https://api.procore.com/rest/v1.0/projects/42?company_id=99' -CompanyId '12233' } 'Wrong company query accepted.'
$FixtureFolder = Join-Path $PSScriptRoot ('test-results/credentials-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($FixtureFolder)
$Fixture = Join-Path $FixtureFolder 'fake.secrets'
[IO.File]::WriteAllText($Fixture,"#Production`nClient ID: fake-id`nClient Secret: fake:secret=value`n#Sandbox`nClient ID: sandbox-id`nClient Secret: sandbox-secret")
$Loaded = Read-ProcoreProductionCredentials $Fixture
Assert ($Loaded['Client ID'] -ceq 'fake-id' -and $Loaded['Client Secret'] -ceq 'fake:secret=value') 'Credential parsing failed.'
foreach ($BadContent in @("#Production`nClient ID: fake-id`nClient ID: duplicate`nClient Secret: fake-secret", "#Sandbox`nClient ID: fake-id`nClient Secret: fake-secret", "#Production`nClient ID: fake-id")) {
    [IO.File]::WriteAllText($Fixture,$BadContent)
    Assert-Rejected { Read-ProcoreProductionCredentials $Fixture } 'Invalid credentials file accepted.'
}
Write-Host ('PASS: ' + $Passed + ' offline Procore checks. No live API access was tested.')
