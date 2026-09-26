$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'ProcoreSandbox.Core.ps1')
. (Join-Path $PSScriptRoot 'ProcoreProduction.Runtime.ps1')
$script:Passed=0
function Assert($Value,$Message) { if (-not $Value) { throw $Message }; $script:Passed++ }
function Reject([scriptblock]$Action) { $Rejected=$false; try { & $Action | Out-Null } catch { $Rejected=$true }; Assert $Rejected 'Expected rejection.' }
$Now=[DateTimeOffset]::Parse('2026-09-25T22:00:00Z')
$Reset=$Now.AddMinutes(10).ToUnixTimeSeconds().ToString()
Assert ((Get-ProcoreDelay @{'X-Rate-Limit-Reset'=$Reset;'X-Rate-Limit-Remaining'='0'} $Now) -eq 603) 'Exhausted budget was not paused.'
Assert ((Get-ProcoreDelay @{'X-Rate-Limit-Reset'=$Reset;'X-Rate-Limit-Remaining'='105'} $Now) -eq 6) 'Adaptive pacing incorrect.'
Assert ((Get-ProcoreDelay @{'Retry-After'='120'} $Now -RateLimited) -eq 123) 'Retry-After seconds ignored.'
Assert ((Get-ProcoreDelay @{'Retry-After'=$Now.AddSeconds(120).ToString('r')} $Now -RateLimited) -eq 123) 'Retry-After date ignored.'
Assert ((Get-ProcoreDelay @{'Date'=$Now.ToString('r');'X-Rate-Limit-Reset'=$Reset} $Now.AddMinutes(3) -RateLimited) -eq 603) 'Clock skew not handled.'
Assert ((Get-ProcoreDelay @{} $Now -RateLimited -Attempt 2) -eq 123) 'Fallback backoff incorrect.'
Assert ((Get-ProcoreDelay @{'X-Rate-Limit-Remaining'='25';'X-Rate-Limit-Reset'=$Now.AddSeconds(10).ToUnixTimeSeconds().ToString()} $Now) -eq 2.5) 'Spike window pacing incorrect.'
function New-Session {
    @{Client=$null;ClientId='fake';Secret='fake-secret';Token='fake';RefreshToken='fake-refresh';CompanyId='12233';
      ExpiresUtc=[DateTimeOffset]::UtcNow.AddHours(1);NextAllowedUtc=[DateTimeOffset]::UtcNow;CooldownPath=$null}
}
function Throw-Status($Status) { $E=[Exception]::new('simulated'); $E.Data['HttpStatus']=$Status; throw $E }
$Session=New-Session
$script:Calls=0; $script:Waits=[Collections.Generic.List[DateTimeOffset]]::new()
$Wait={param($Until) $script:Waits.Add($Until)}
$Send={param($S,$U,$H) $script:Calls++; if ($script:Calls -eq 1) { $H['Retry-After']='60'; Throw-Status 429 }; [pscustomobject]@{id=42} }
$Result=Invoke-ProcoreReliableGet $Session 'unused' -Wait $Wait -Send $Send
Assert ($Result.id -eq 42 -and $script:Calls -eq 2 -and $script:Waits[1] -gt $script:Waits[0].AddSeconds(60)) '429 did not wait and retry.'
$script:Calls=0
Reject { Invoke-ProcoreReliableGet (New-Session) 'unused' -Wait $Wait -Send {param($S,$U,$H) $script:Calls++; Throw-Status 403} }
Assert ($script:Calls -eq 1) 'Permission errors retried.'
$script:Calls=0
Reject { Invoke-ProcoreReliableGet (New-Session) 'unused' -Wait $Wait -Send {param($S,$U,$H) $script:Calls++; Throw-Status 503} }
Assert ($script:Calls -eq 6) 'Transient retries were not bounded.'
$script:Calls=0; $script:Refreshes=0
$Refresh={param($S) $script:Refreshes++; $S.Token='new'; $S.ExpiresUtc=[DateTimeOffset]::UtcNow.AddHours(1)}
$Result=Invoke-ProcoreReliableGet (New-Session) 'unused' -Wait $Wait -Refresh $Refresh -Send {param($S,$U,$H) $script:Calls++; if ($script:Calls -eq 1) { Throw-Status 401 }; [pscustomobject]@{id=1} }
Assert ($script:Calls -eq 2 -and $script:Refreshes -eq 1) '401 refresh failed.'
$Session=New-Session; $Session.ExpiresUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1); $script:Refreshes=0
$Result=Invoke-ProcoreReliableGet $Session 'unused' -Wait $Wait -Refresh $Refresh -Send {param($S,$U,$H) [pscustomobject]@{token=$S.Token} }
Assert ($Result.token -eq 'new' -and $script:Refreshes -eq 1) 'Expiry refresh failed.'
$Session=New-Session
Set-ProcoreSessionTokens $Session ([pscustomobject]@{access_token='rotated';refresh_token='rotated-refresh';expires_in=7200})
Assert ($Session.Token -ceq 'rotated' -and $Session.RefreshToken -ceq 'rotated-refresh' -and $Session.ExpiresUtc -gt [DateTimeOffset]::UtcNow.AddMinutes(119)) 'Token rotation failed.'
$TokenPath = Join-Path ([IO.Path]::GetTempPath()) ('procore-refresh-' + [guid]::NewGuid().ToString('N'))
Save-ProcoreRefreshToken $TokenPath 'refresh-one'
$Stored = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($TokenPath))
Assert ($Stored -notmatch 'refresh-one') 'Refresh token was stored in plaintext.'
Assert ((Read-ProcoreRefreshToken $TokenPath) -ceq 'refresh-one') 'Saved refresh token could not be read.'
$Session = New-Session
$Session.TokenFile = $TokenPath
Set-ProcoreSessionTokens $Session ([pscustomobject]@{access_token='rotated';refresh_token='rotated-refresh';expires_in=7200})
Assert ((Read-ProcoreRefreshToken $TokenPath) -ceq 'rotated-refresh') 'Rotated refresh token was not saved.'
Remove-Item -LiteralPath $TokenPath -Force
Assert ($null -eq (Read-ProcoreRefreshToken $TokenPath)) 'A missing refresh token was returned.'

# Actual HTTP adapter exercised through the reliable path, including singleton/empty array preservation.
$Fake=[pscustomobject]@{Body='[]'}
$Fake | Add-Member ScriptMethod SendAsync {
    param($Request)
    $Response=[Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
    $Response.Content=[Net.Http.StringContent]::new($this.Body)
    $Response.Headers.Add('X-Rate-Limit-Remaining','0')
    $Response.Headers.Add('X-Rate-Limit-Reset',[DateTimeOffset]::UtcNow.AddSeconds(60).ToUnixTimeSeconds().ToString())
    $Completion=[Threading.Tasks.TaskCompletionSource[Net.Http.HttpResponseMessage]]::new()
    $Completion.SetResult($Response); return $Completion.Task
}
$Session=New-Session; $Session.Client=$Fake
$Result=Invoke-ProcoreReliableGet $Session 'https://api.procore.com/rest/v1.0/companies/12233/projects' -Wait $Wait
Assert ($Result -is [Array] -and $Result.Count -eq 0 -and $Session.NextAllowedUtc -gt [DateTimeOffset]::UtcNow.AddSeconds(55)) 'HTTP budget headers/empty array lost.'
$Fake.Body='[{"id":42}]'
$Result=Invoke-ProcoreReliableGet $Session 'https://api.procore.com/rest/v1.0/companies/12233/projects' -Wait $Wait
Assert ($Result -is [Array] -and $Result.Count -eq 1) 'Single array lost through reliable adapter.'

# Real local sockets, only fake callback data; no browser, credentials, or Procore calls.
$script:BrowserClient=[Net.Http.HttpClient]::new()
$script:CallbackTask=$null
try {
    $Code=Receive-ProcoreCallback 'unused' 'fake-state' -TimeoutSeconds 5 -OpenBrowser {
        param($Url)
        $script:CallbackTask=$script:BrowserClient.GetAsync('http://localhost/?code=fake-code&state=fake-state')
    }
    $Response=$script:CallbackTask.GetAwaiter().GetResult()
    Assert ($Code -ceq 'fake-code' -and [int]$Response.StatusCode -eq 200) 'Automatic localhost callback failed.'
    $Response.Dispose()
    Reject { Receive-ProcoreCallback 'unused' 'expected-state' -TimeoutSeconds 1 -OpenBrowser {
        param($Url)
        $script:CallbackTask=$script:BrowserClient.GetAsync('http://localhost/?code=fake-code&state=wrong-state')
    } }
    $Response=$script:CallbackTask.GetAwaiter().GetResult()
    Assert ([int]$Response.StatusCode -eq 400) 'Wrong-state callback was accepted.'
    $Response.Dispose()
    $Probe=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,80)
    try { $Probe.Start(); Assert $true 'Port released after timeout.' } finally { $Probe.Stop() }
} finally { $script:BrowserClient.Dispose() }
Write-Host ('PASS: ' + $script:Passed + ' reliability checks; no live Procore requests.')
