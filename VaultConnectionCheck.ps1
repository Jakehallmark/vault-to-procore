param(
    [string]$ClientFolder = 'C:\Program Files\Autodesk\Vault Client 2024\Explorer',
    [string]$Server,
    [string]$Vault,
    [switch]$ScanProjects
)

$ErrorActionPreference = 'Stop'
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
    if ([string]::IsNullOrWhiteSpace($Server)) { $Server = Read-Host 'Server shown in your Vault login dialog' }
    if ([string]::IsNullOrWhiteSpace($Vault)) { $Vault = Read-Host 'Vault database name shown in your Vault login dialog' }
    if ([string]::IsNullOrWhiteSpace($Server) -or [string]::IsNullOrWhiteSpace($Vault)) {
        throw 'Both the server and Vault database name are required.'
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
