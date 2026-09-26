$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'VaultConnectionCheck.ps1')
$Passed = 0
function Assert($Value, $Message) { if (-not $Value) { throw $Message }; $script:Passed++ }
$Root = Join-Path ([IO.Path]::GetTempPath()) ('vault-login-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($Root)
try {
    $Path = Join-Path $Root 'ApplicationPreferences.xml'
    @'
<Categories>
  <Category ID="Login">
    <Property Name="AutoLogin" Value="True" />
    <Property Name="ServerName" Value="vault.example" />
    <Property Name="SelectedAuthenticationType" Value="1" />
    <Property Name="Password" Value="do-not-read" />
    <Property Name="DatabaseName" Value="Designs" />
  </Category>
</Categories>
'@ | Set-Content -LiteralPath $Path -Encoding UTF8
    $Login = Get-VaultProfessionalLogin -Path $Path
    Assert ($Login.Server -ceq 'vault.example' -and $Login.Database -ceq 'Designs') 'Saved Vault server and database were not read.'
    Assert (@($Login.PSObject.Properties.Name) -notcontains 'Password') 'The saved Vault password was read.'
    @'
<Categories><Category ID="Login">
  <Property Name="ServerName" Value="vault.example" />
  <Property Name="DatabaseName" Value="Designs" />
  <Property Name="SelectedAuthenticationType" Value="0" />
</Category></Categories>
'@ | Set-Content -LiteralPath $Path -Encoding UTF8
    $Refused = $false
    try { Get-VaultProfessionalLogin -Path $Path | Out-Null } catch { $Refused = $true }
    Assert $Refused 'A non-Windows Vault login was accepted.'
    Assert ($null -eq (Get-VaultProfessionalLogin -Path (Join-Path $Root 'missing.xml'))) 'A missing preferences file returned a login.'
    $Secrets = Join-Path $Root '.secrets'
    @'
#Production
Client ID: app-id
Client Secret: hidden
#Vault Client
ClientFolder: C:\Program Files\Autodesk\Vault Client 2024\Explorer
Server: https://psvault2024.ps.local
Vault: DI_Vault
'@ | Set-Content -LiteralPath $Secrets -Encoding UTF8
    $FromFile = Get-VaultClientSecrets -Path $Secrets
    Assert ($FromFile.ClientFolder -ceq 'C:\Program Files\Autodesk\Vault Client 2024\Explorer' -and $FromFile.Server -ceq 'https://psvault2024.ps.local' -and $FromFile.Vault -ceq 'DI_Vault') 'Vault client settings were not read from .secrets.'
    Assert ($null -eq (Get-VaultClientSecrets -Path (Join-Path $Root 'missing.secrets'))) 'A missing .secrets file returned Vault client settings.'
}
finally { Remove-Item -LiteralPath $Root -Recurse -Force }
Write-Host ("PASS " + $Passed)
