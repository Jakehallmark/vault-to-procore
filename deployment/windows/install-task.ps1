param(
    [string]$InstallDirectory = "$env:ProgramData\VaultToProcore",
    [int]$IntervalMinutes = 15,
    [string]$ServiceAccount = "SYSTEM"
)

$ErrorActionPreference = "Stop"
# Task registration and ProgramData installation both require elevation. Fail up
# front instead of leaving behind half of an installation.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this installer from an elevated PowerShell session."
}

$Source = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
# Runtime data stays outside the application source tree so upgrades do not wipe
# the ledger, logs, or customer configuration.
New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
New-Item -ItemType Directory -Force -Path "$InstallDirectory\logs" | Out-Null

$Executable = Join-Path $Source "dist\vault-to-procore.exe"
if (-not (Test-Path $Executable)) { throw "Build dist\vault-to-procore.exe first with deployment\build.ps1" }
Copy-Item $Executable "$InstallDirectory\vault-to-procore.exe" -Force
if (-not (Test-Path "$InstallDirectory\.env")) { Copy-Item "$Source\.env.example" "$InstallDirectory\.env" }

$Action = New-ScheduledTaskAction -Execute "$InstallDirectory\vault-to-procore.exe" -Argument "--live" -WorkingDirectory $InstallDirectory
# IgnoreNew works with the application's own PID lock. The scheduler avoids an
# overlap first, and the application still protects itself if launched by hand.
$Trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$Settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5)
$Principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName "VaultToProcoreSync" -Action $Action -Trigger $Trigger -Settings $Settings -Principal $Principal -Force | Out-Null
Write-Host "Installed VaultToProcoreSync. Edit $InstallDirectory\.env, then run the task manually to validate it."
