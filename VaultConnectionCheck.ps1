param(
    [string]$ClientFolder = 'C:\Program Files\Autodesk\Vault Client 2024\Explorer',
    [string]$Server = 'https://psvault2024.ps.local',
    [string]$Vault = 'DI_Vault',
    [string]$PreferencesPath,
    [switch]$ScanProjects
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

if ($MyInvocation.InvocationName -eq '.') { return }

if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) {
    throw 'Run this check in 64-bit Windows PowerShell 5.1, not PowerShell 7.'
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
    $Remembered = Get-VaultProfessionalLogin -Path $PreferencesPath
    if ($null -ne $Remembered) {
        $Server = $Remembered.Server
        $Vault = $Remembered.Database
        Write-Host ('Using the server and database saved by Vault Professional: ' + $Server + ' / ' + $Vault)
    }
    elseif ([string]::IsNullOrWhiteSpace($Server) -or [string]::IsNullOrWhiteSpace($Vault)) {
        throw 'Vault Professional has no saved server and database. Sign in to Vault Professional, then scan again.'
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
        & (Join-Path $PSScriptRoot 'ScanVaultProjects.ps1') -Connection $Connection
    }
}
finally {
    try {
        if ($null -ne $Connection -and $null -ne $Manager) { $Manager.LogOut($Connection) }
    }
    finally { [VaultCheckLoader]::Stop() }
}
