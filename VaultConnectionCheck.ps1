param(
    [string]$ClientFolder = 'C:\Program Files\Autodesk\Vault Client 2024\Explorer',
    [string]$Server = 'https://psvault2024.ps.local',
    [string]$Vault = 'DI_Vault'
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) {
    throw 'Run this check in 64-bit PowerShell 5.1, not PowerShell 7'
}

$LibraryNames = @(
    'Autodesk.Connectivity.WebServices.dll'
    ##'Autodesk.Connectivity.WebServicesTools.dll'
    'Autodesk.DataManagement.Client.Framework.dll'
    'Autodesk.DataManagement.Client.Framework.Vault.dll'
)
$Missing = @($LibraryNames | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ClientFolder $_) -PathType Leaf) })
if ($Missing.Count -gt 0) {
    throw ('Required installed Vault libraries were not found in ' + $ClientFolder + ': ' + ($Missing -join ','))
}
foreach ($Name in $LibraryNames) {
    $File = Get-Item -LiteralPath (Join-Path $ClientFolder $Name)
    Write-Host ($Name + ' | file version ' + $File.VersionInfo.FileVersion)
}

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
public static class VaultCheckLoader
{
    private static string folder;
    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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
        if (name.IndexOfAny(new char [] { '/', '\\', ':' }) >= 0) 
            return null;
        
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
    if ([string]::IsNullOrWhiteSpace($Server)) { $Server = Read-Host 'Server shown in Vault login' }
    if ([string]::IsNullOrWhiteSpace($Vault)) { $Vault = Read-Host 'Vault DB name' }
    if ([string]::IsNullOrWhiteSpace($Server) -or ([string]::IsNullOrWhiteSpace($Vault))) {
        throw 'Both the server and Vault database names are required.'
    }

$LibraryType = [Autodesk.DataManagement.Client.Framework.Vault.Library]

Write-Host 'Loaded DLL:' $LibraryType.Assembly.Location

$LibraryType.GetMembers() |
    Where-Object { $_.Name -match 'ConnectionManager|Initialize' } |
    ForEach-Object { Write-Host $_.ToString() }

[Autodesk.DataManagement.Client.Framework.Vault.Library]::Initialize($false)
$Manager = [Autodesk.DataManagement.Client.Framework.Vault.Library]::ConnectionManager
    if ($null -eq $Manager) {
        throw 'Vault Connection Manager is null.'
    }
$Flags = [Autodesk.DataManagement.Client.Framework.Vault.Currency.Connections.AuthenticationFlags]::WindowsAuthentication -bor
        [Autodesk.DataManagement.Client.Framework.Vault.Currency.Connections.AuthenticationFlags]::ReadOnly
$Result = $Manager.Login($Server.Trim(), $Vault.Trim(), '', '', $Flags, $null)
if (-not $Result.Success) {
    Write-Host 'Windows Auth did not establish a connection.'
    foreach ($ErrorItem in $Result.ErrorMessages.GetEnumerator()) {
        Write-Host ($ErrorItem.Key.ToString() + ': ' + $ErrorItem.Value)
    }
    if ($null -ne $Result.Exception) { Write-Host $Result.Exception.Message }
    throw 'Vault connection check failed.'
}

$Connection = $Result.Connection
$RootFolder = $Connection.WebServiceManager.DocumentService.GetFolderRoot()
if ($null -eq $RootFolder) { throw 'Login succeeded, but returned no root folder' }
    Write-Host ('Success! ' + $RootFolder.Id + '.')
 
    & (Join-Path $PSScriptRoot 'TestVaultProjectCopy.ps1') -Connection $Connection

}

finally {

    try {

        if ($null -ne $Connection -and $null -ne $Manager) {

            $Manager.LogOut($Connection)

        }

    }

    finally {

        [VaultCheckLoader]::Stop()

    }

}
 