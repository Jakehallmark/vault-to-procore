# Production-only runtime. Access tokens stay in memory. A refresh token may be stored with Windows DPAPI for this user.
if (-not ('VaultTransferProtect' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class VaultTransferProtect
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int cbData; public IntPtr pbData; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
    public static byte[] Protect(byte[] input) { return Convert(input, true); }
    public static byte[] Unprotect(byte[] input) { return Convert(input, false); }
    private static byte[] Convert(byte[] input, bool protect)
    {
        var data = new Blob();
        data.pbData = Marshal.AllocHGlobal(input.Length);
        data.cbData = input.Length;
        Marshal.Copy(input, 0, data.pbData, input.Length);
        var result = new Blob();
        try
        {
            bool ok = protect
                ? CryptProtectData(ref data, "Vault Transfer Procore", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref result)
                : CryptUnprotectData(ref data, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref result);
            if (!ok) throw new Win32Exception();
            var output = new byte[result.cbData];
            Marshal.Copy(result.pbData, output, 0, result.cbData);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(data.pbData);
            if (result.pbData != IntPtr.Zero) LocalFree(result.pbData);
        }
    }
}
'@
}

function Save-ProcoreRefreshToken([string]$Path, [string]$Token) {
    if (-not $Path -or -not $Token) { throw 'A refresh token path and value are required.' }
    $Folder = Split-Path -Parent $Path
    if ($Folder) { [void][IO.Directory]::CreateDirectory($Folder) }
    $Plain = [Text.Encoding]::UTF8.GetBytes($Token)
    try {
        $Protected = [VaultTransferProtect]::Protect($Plain)
        $Temp = $Path + '.tmp'
        [IO.File]::WriteAllBytes($Temp, $Protected)
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($Temp, $Path, [NullString]::Value) }
        else { [IO.File]::Move($Temp, $Path) }
    }
    finally { [Array]::Clear($Plain, 0, $Plain.Length) }
}

function Read-ProcoreRefreshToken([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        $Plain = [VaultTransferProtect]::Unprotect([IO.File]::ReadAllBytes($Path))
        try { return [Text.Encoding]::UTF8.GetString($Plain).Trim() }
        finally { [Array]::Clear($Plain, 0, $Plain.Length) }
    }
    catch { return $null }
}
function Wait-ProcoreUntil([DateTimeOffset]$Until) {
    if ($Until -gt [DateTimeOffset]::UtcNow.AddSeconds(5)) {
        Write-Host ('Waiting for Procore until ' + $Until.ToLocalTime().ToString('h:mm:ss tt') + '. Ctrl+C stops safely; completed project checkpoints are retained.')
    }
    while ($Until -gt [DateTimeOffset]::UtcNow) {
        Start-Sleep -Milliseconds ([int][Math]::Min(1000, [Math]::Max(1, ($Until - [DateTimeOffset]::UtcNow).TotalMilliseconds)))
    }
}

function Get-ProcoreDelay {
    param([hashtable]$Headers, [DateTimeOffset]$Now, [int]$Attempt = 0, [switch]$RateLimited)
    $ServerNow = $Now
    $Date = [DateTimeOffset]::MinValue
    if ($Headers['Date'] -and [DateTimeOffset]::TryParse($Headers['Date'], [ref]$Date)) { $ServerNow = $Date }
    $ResetSeconds = 0.0; $Epoch = 0L
    if ([long]::TryParse([string]$Headers['X-Rate-Limit-Reset'], [ref]$Epoch) -and $Epoch -gt 0 -and $Epoch -lt 253402300799) {
        $ResetSeconds = [Math]::Max(0, ([DateTimeOffset]::FromUnixTimeSeconds($Epoch) - $ServerNow).TotalSeconds)
    }
    $RetrySeconds = 0.0; $Seconds = 0.0
    if ([double]::TryParse([string]$Headers['Retry-After'], [ref]$Seconds) -and $Seconds -ge 0 -and -not [double]::IsInfinity($Seconds) -and -not [double]::IsNaN($Seconds)) {
        $RetrySeconds = $Seconds
    } elseif ($Headers['Retry-After'] -and [DateTimeOffset]::TryParse($Headers['Retry-After'], [ref]$Date)) {
        $RetrySeconds = [Math]::Max(0, ($Date - $ServerNow).TotalSeconds)
    }
    $Remaining = 0L
    $HasRemaining = [long]::TryParse([string]$Headers['X-Rate-Limit-Remaining'], [ref]$Remaining)
    if ($RateLimited -or ($HasRemaining -and $Remaining -le 5)) {
        $Delay = [Math]::Max($RetrySeconds, $ResetSeconds)
        if ($Delay -le 0) { $Delay = [Math]::Min(300, 30 * [Math]::Pow(2, $Attempt)) }
        return $Delay + 3
    }
    if ($Attempt -gt 0) { return [Math]::Max($RetrySeconds, [Math]::Min(60, [Math]::Pow(2, $Attempt))) + 1 }
    # Spread the remaining budget over the reset window, reserving five calls.
    if ($HasRemaining -and $Remaining -gt 5 -and $ResetSeconds -gt 0) {
        return [Math]::Max(2.5, $ResetSeconds / ($Remaining - 5))
    }
    return [Math]::Max(2.5, $RetrySeconds)
}

function Set-ProcoreSessionTokens($Session, $Reply) {
    if ($null -eq $Reply -or $Reply.PSObject.Properties.Name -notcontains 'access_token' -or
        [string]::IsNullOrWhiteSpace($Reply.access_token)) { throw 'Procore did not return an access token.' }
    $Lifetime = 3600.0
    if ($Reply.PSObject.Properties.Name -contains 'expires_in') {
        if (-not [double]::TryParse([string]$Reply.expires_in, [ref]$Lifetime) -or $Lifetime -le 0 -or $Lifetime -gt 31536000) {
            throw 'Unexpected token expiry.'
        }
    }
    $Session.Token = [string]$Reply.access_token
    if ($Reply.PSObject.Properties.Name -contains 'refresh_token' -and $Reply.refresh_token) { $Session.RefreshToken = [string]$Reply.refresh_token }
    $Session.ExpiresUtc = [DateTimeOffset]::UtcNow.AddSeconds($Lifetime)
    if ($Session -is [hashtable] -and $Session.Contains('TokenFile') -and $Session.TokenFile -and $Session.RefreshToken) {
        Save-ProcoreRefreshToken $Session.TokenFile $Session.RefreshToken
    }
}

function Update-ProcoreSessionToken($Session) {
    if (-not $Session.RefreshToken) { throw 'Sign-in expired and no refresh token was returned. Resume with a new sign-in.' }
    $Fields = @{grant_type='refresh_token'; client_id=$Session.ClientId; client_secret=$Session.Secret; refresh_token=$Session.RefreshToken}
    try {
        # Never automatically replay token POSTs after an uncertain result.
        $Reply = Invoke-ProcoreRequest -Client $Session.Client -Environment Production -Method POST -Url 'https://login.procore.com/oauth/token' -Form $Fields
        Set-ProcoreSessionTokens $Session $Reply
    } finally { $Fields.Clear(); $Reply = $null }
}

function Invoke-ProcoreReliableGet {
    param($Session, [string]$Url,
        [scriptblock]$Wait = { param($Until) Wait-ProcoreUntil $Until },
        [scriptblock]$Send = { param($S,$U,$H) Invoke-ProcoreRequest -Client $S.Client -Environment Production -Method GET -Url $U -Token $S.Token -CompanyId $S.CompanyId -ResponseMetadata $H },
        [scriptblock]$Refresh = { param($S) Update-ProcoreSessionToken $S })
    $Refreshed = $false
    for ($Attempt=0; $Attempt -le 5; $Attempt++) {
        & $Wait $Session.NextAllowedUtc
        if ($Session.ExpiresUtc -le [DateTimeOffset]::UtcNow.AddSeconds(60)) { & $Refresh $Session }
        $Headers = @{}
        try {
            $Data = & $Send $Session $Url $Headers
            $Delay = Get-ProcoreDelay $Headers ([DateTimeOffset]::UtcNow)
            $Session.NextAllowedUtc = [DateTimeOffset]::UtcNow.AddSeconds($Delay)
            if ($Delay -gt 10) { Save-ProcoreCooldown $Session }
            return ,$Data
        } catch {
            $Status = $_.Exception.Data['HttpStatus']
            if ($Status -eq 401 -and -not $Refreshed) {
                & $Refresh $Session; $Refreshed = $true; continue
            }
            $Retry = $Status -in @(408,429,500,502,503,504) -or $_.Exception.Data['TransientNetwork']
            if (-not $Retry) { throw }
            $Delay = Get-ProcoreDelay $Headers ([DateTimeOffset]::UtcNow) -Attempt ($Attempt+1) -RateLimited:($Status -eq 429)
            $Delay += (Get-Random -Minimum 0 -Maximum 1000) / 1000.0
            $Session.NextAllowedUtc = [DateTimeOffset]::UtcNow.AddSeconds($Delay)
            Save-ProcoreCooldown $Session
            if ($Attempt -eq 5) { throw 'Procore remained unavailable after bounded retries. Completed checkpoints are retained; resume later.' }
            Write-Host ('Procore request paused; retry ' + ($Attempt+1) + ' of 5 will run automatically.')
        }
    }
    throw 'Authentication retry limit reached. Resume with a new sign-in.'
}

function Save-ProcoreCooldown($Session) {
    if ($Session.CooldownPath) {
        $Text = @{CompanyId=$Session.CompanyId; NextAllowedUtc=$Session.NextAllowedUtc.ToString('o')} | ConvertTo-Json
        [IO.File]::WriteAllText($Session.CooldownPath + '.tmp', $Text)
        [IO.File]::Move($Session.CooldownPath + '.tmp', $Session.CooldownPath, $true)
    }
}

function Receive-ProcoreCallback {
    param([string]$AuthUrl, [string]$State, [int]$TimeoutSeconds = 600,
        [scriptblock]$OpenBrowser = { param($Url) Start-Process $Url })
    # Explicit loopback sockets avoid Windows HTTP URL reservations and administrator rights.
    $Listeners = [Collections.Generic.List[Net.Sockets.TcpListener]]::new()
    try {
        foreach ($Address in @([Net.IPAddress]::Loopback, [Net.IPAddress]::IPv6Loopback)) {
            $Listener = [Net.Sockets.TcpListener]::new($Address,80)
            $Listener.Server.ExclusiveAddressUse = $true
            try { $Listener.Start(); $Listeners.Add($Listener) }
            catch { $Listener.Stop(); throw 'Cannot listen on localhost port 80. Close the program using it, or run with -ManualCallback.' }
        }
        & $OpenBrowser $AuthUrl
        $Deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([DateTimeOffset]::UtcNow -lt $Deadline) {
            foreach ($Listener in $Listeners) {
                if (-not $Listener.Pending()) { continue }
                $Peer = $Listener.AcceptTcpClient()
                $Denied = $false
                try {
                    $Stream = $Peer.GetStream(); $Stream.ReadTimeout = 2000; $Stream.WriteTimeout = 2000
                    $Bytes = [Collections.Generic.List[byte]]::new()
                    $ReadDeadline = [DateTimeOffset]::UtcNow.AddSeconds(3)
                    while ($Bytes.Count -lt 16384 -and [DateTimeOffset]::UtcNow -lt $ReadDeadline -and [DateTimeOffset]::UtcNow -lt $Deadline) {
                        $Byte = $Stream.ReadByte()
                        if ($Byte -lt 0) { break }
                        $Bytes.Add([byte]$Byte)
                        $N = $Bytes.Count
                        if ($N -ge 4 -and $Bytes[$N-4] -eq 13 -and $Bytes[$N-3] -eq 10 -and $Bytes[$N-2] -eq 13 -and $Bytes[$N-1] -eq 10) { break }
                    }
                    $Request = [Text.Encoding]::ASCII.GetString($Bytes.ToArray())
                    $Code = $null
                    if ($Request -match '^GET (/\?[^\s]+) HTTP/1\.[01]\r\n' -and
                        $Request -match '(?im)^Host: localhost(?::80)?\r?$') {
                        $Target = ($Request -split "`r`n")[0].Split(' ')[1]
                        try { $Code = Get-ProcoreAuthorizationCode ('http://localhost' + $Target) $State }
                        catch { $Denied = $_.Exception.Message -eq 'Procore authorization was declined or failed. No project query was made.' }
                    }
                    $Body = if ($Code) { 'Sign-in received. You can close this tab and return to Vault Transfer.' } else { 'This request was not accepted. Return to the Procore sign-in tab.' }
                    $Status = if ($Code) { '200 OK' } else { '400 Bad Request' }
                    $Reply = [Text.Encoding]::UTF8.GetBytes("HTTP/1.1 $Status`r`nContent-Type: text/plain; charset=utf-8`r`nCache-Control: no-store`r`nReferrer-Policy: no-referrer`r`nConnection: close`r`nContent-Length: $([Text.Encoding]::UTF8.GetByteCount($Body))`r`n`r`n$Body")
                    $Stream.Write($Reply,0,$Reply.Length)
                    if ($Code) { return $Code }
                } catch { } finally { $Peer.Dispose() }
                if ($Denied) { throw 'Procore authorization was declined. Run again when ready.' }
            }
            Start-Sleep -Milliseconds 100
        }
        throw 'Sign-in timed out or was declined. Run again when ready.'
    } finally { foreach ($Listener in $Listeners) { $Listener.Stop() } }
}
