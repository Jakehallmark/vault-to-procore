param(
    [string]$ClientFolder = 'C:\Program Files\Autodesk\Vault Client 2024\Explorer',
    [string]$Server = 'https://psvault2024.ps.local',
    [string]$Vault = 'DI_Vault',
    [string]$PreferencesPath,
    [switch]$ScanProjects,
    [switch]$ProjectsOnly,
    [string]$ChangedSince = ''
)

$ErrorActionPreference = 'Stop'

function Get-VaultProfessionalLogin {
    param([string]$Path)
    if (-not $Path) {
        $Path = Join-Path $env:APPDATA 'Autodesk\Autodesk Vault Professional 2024\ApplicationPreferences.xml'
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $Xml = New-Object xml
    $Xml.Load($Path)
    $Category = $Xml.SelectSingleNode("//Category[@ID='Login']")
    if ($null -eq $Category) { return $null }
    $ServerNode = $Category.SelectSingleNode("Property[@Name='ServerName']")
    $DatabaseNode = $Category.SelectSingleNode("Property[@Name='DatabaseName']")
    $ServerName = if ($null -eq $ServerNode) { '' } else { ([string]$ServerNode.GetAttribute('Value')).Trim() }
    $DatabaseName = if ($null -eq $DatabaseNode) { '' } else { ([string]$DatabaseNode.GetAttribute('Value')).Trim() }
    if (-not $ServerName -or -not $DatabaseName) { return $null }
    $AuthNode = $Category.SelectSingleNode("Property[@Name='SelectedAuthenticationType']")
    $Auth = if ($null -eq $AuthNode) { '' } else { ([string]$AuthNode.GetAttribute('Value')).Trim() }
    if ($Auth -and $Auth -ne '1') {
        throw 'Vault Professional is not set to Windows authentication. Sign in there with Windows Account, then scan again.'
    }
    return [pscustomobject]@{ Server = $ServerName; Database = $DatabaseName }
}

function Get-VaultClientSecrets {
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $Section = $false
    $ClientFolder = ''
    $Server = ''
    $Vault = ''
    foreach ($Raw in [IO.File]::ReadAllLines($Path)) {
        $Line = $Raw.Trim()
        if (-not $Line) { continue }
        if ($Line.StartsWith('#')) {
            $Section = $Line -eq '#Vault Client'
            continue
        }
        if (-not $Section) { continue }
        $Index = $Line.IndexOf(':')
        if ($Index -lt 1) { throw 'Invalid Vault client entry. Expected ClientFolder:, Server:, or Vault:.' }
        $Key = $Line.Substring(0, $Index).Trim()
        $Value = $Line.Substring($Index + 1).Trim()
        switch ($Key) {
            'ClientFolder' { if ($ClientFolder) { throw 'Duplicate Vault client entry.' }; $ClientFolder = $Value }
            'Server' { if ($Server) { throw 'Duplicate Vault client entry.' }; $Server = $Value }
            'Vault' { if ($Vault) { throw 'Duplicate Vault client entry.' }; $Vault = $Value }
            default { throw 'Invalid Vault client entry. Expected ClientFolder:, Server:, or Vault:.' }
        }
    }
    if (-not $ClientFolder -and -not $Server -and -not $Vault) { return $null }
    return [pscustomobject]@{ ClientFolder = $ClientFolder; Server = $Server; Vault = $Vault }
}

if ($MyInvocation.InvocationName -eq '.') { return }

if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) {
    throw 'Run this check in 64-bit Windows PowerShell 5.1, not PowerShell 7.'
}

$FromSecrets = Get-VaultClientSecrets -Path (Join-Path $PSScriptRoot '.secrets')
$UsingSecrets = $false
if ($null -ne $FromSecrets) {
    if ($FromSecrets.ClientFolder) { $ClientFolder = $FromSecrets.ClientFolder }
    if ($FromSecrets.Server) { $Server = $FromSecrets.Server }
    if ($FromSecrets.Vault) { $Vault = $FromSecrets.Vault }
    $UsingSecrets = [bool]$FromSecrets.Server -and [bool]$FromSecrets.Vault
    if ($UsingSecrets) { Write-Host ('Using the Vault server and database in .secrets: ' + $Server + ' / ' + $Vault) }
}

# Load the installed Vault libraries in their supported .NET Framework host.
# No credentials are read from the running Vault client or stored in this file.
$LibraryNames = @(
    'Autodesk.Connectivity.WebServices.dll',
    'Autodesk.DataManagement.Client.Framework.dll',
    'Autodesk.DataManagement.Client.Framework.Vault.dll'
)
$Missing = @($LibraryNames | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ClientFolder $_) -PathType Leaf) })
if ($Missing.Count -gt 0) {
    throw ('Required installed Vault libraries were not found in ' + $ClientFolder + ': ' + ($Missing -join ', '))
}
foreach ($Name in $LibraryNames) {
    $File = Get-Item -LiteralPath (Join-Path $ClientFolder $Name)
    Write-Host ($Name + ' | file version ' + $File.VersionInfo.FileVersion)
}

# Resolve dependencies on any SDK thread without relying on a PowerShell runspace.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
public static class VaultCheckLoader
{
    private static string folder;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);
    public static void Start(string path)
    {
        folder = Path.GetFullPath(path);
        if (!SetDllDirectory(folder)) throw new System.ComponentModel.Win32Exception();
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name.IndexOfAny(new char[] { '/', '\\', ':' }) >= 0) return null;
        string path = Path.Combine(folder, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
    public static void Stop()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
        SetDllDirectory(null);
    }
}
'@

$Connection = $null
$Manager = $null
[VaultCheckLoader]::Start($ClientFolder)
try {
    foreach ($Name in $LibraryNames) {
        [void][Reflection.Assembly]::LoadFrom((Join-Path $ClientFolder $Name))
    }
    if (-not $UsingSecrets) {
        $Remembered = Get-VaultProfessionalLogin -Path $PreferencesPath
        if ($null -ne $Remembered) {
            $Server = $Remembered.Server
            $Vault = $Remembered.Database
            Write-Host ('Using the server and database saved by Vault Professional: ' + $Server + ' / ' + $Vault)
        }
        else { Write-Host ('Using ' + $Server + ' / ' + $Vault) }
    }
    if ([string]::IsNullOrWhiteSpace($Server) -or [string]::IsNullOrWhiteSpace($Vault)) {
        throw 'Vault server and database are not set. Add them under #Vault Client in .secrets.'
    }

    [Autodesk.DataManagement.Client.Framework.Vault.Library]::Initialize($false)
    $Manager = [Autodesk.DataManagement.Client.Framework.Vault.Library]::ConnectionManager
    if ($null -eq $Manager) {
        throw 'Vault ConnectionManager is still null after SDK initialization.'
    }
    $Flags = [Autodesk.DataManagement.Client.Framework.Vault.Currency.Connections.AuthenticationFlags]::WindowsAuthentication -bor
             [Autodesk.DataManagement.Client.Framework.Vault.Currency.Connections.AuthenticationFlags]::ReadOnly
    $Result = $Manager.LogIn($Server.Trim(), $Vault.Trim(), '', '', $Flags, $null)
    if (-not $Result.Success) {
        Write-Host 'Windows authentication did not establish a connection.'
        Write-Host 'Check the VPN, the login authentication selection, the server, and the Vault database name.'
        foreach ($ErrorItem in $Result.ErrorMessages.GetEnumerator()) {
            Write-Host ($ErrorItem.Key.ToString() + ': ' + $ErrorItem.Value)
        }
        if ($null -ne $Result.Exception) { Write-Host $Result.Exception.Message }
        throw 'Vault connection check failed. No alternate credentials or login methods were attempted.'
    }

    $Connection = $Result.Connection
    $RootFolder = $Connection.WebServiceManager.DocumentService.GetFolderRoot()
    if ($null -eq $RootFolder) { throw 'Login succeeded but Vault returned no root folder.' }
    Write-Host ('SUCCESS: Windows login accepted; read-only Vault root query returned folder ID ' + $RootFolder.Id + '.')
    Write-Host 'No document was downloaded, changed, checked out, or uploaded.'
    if ($ScanProjects) {
        $ScanArguments = @{ Connection = $Connection }
        if ($ChangedSince) { $ScanArguments.ChangedSince = $ChangedSince }
        if ($ProjectsOnly) { $ScanArguments.ProjectsOnly = $true }
        & (Join-Path $PSScriptRoot 'ScanVaultProjects.ps1') @ScanArguments
    }
}
finally {
    try {
        if ($null -ne $Connection -and $null -ne $Manager) { $Manager.LogOut($Connection) }
    }
    finally { [VaultCheckLoader]::Stop() }
}
