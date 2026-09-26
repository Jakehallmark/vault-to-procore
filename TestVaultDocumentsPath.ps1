$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'VaultDocumentsPath.ps1')
$Project = '$/Designs/Projects/101000-101999/101097 - WM Wave 23 - Walmart Store 2151 1250kw'
$Relative = Get-VaultDocumentsRelativePath -ProjectPath $Project -VaultFilePath ($Project + '/Settings Files/PLC/PowerBlock 1/relay.zap15')
if ($Relative -cne 'Settings Files/PLC/PowerBlock 1/relay.zap15') { throw 'Child folders were not preserved.' }
if ($Relative.StartsWith('101097')) { throw 'Project folder was included in Documents.' }
$RootFile = Get-VaultDocumentsRelativePath -ProjectPath $Project -VaultFilePath ($Project + '/one-line.pdf')
if ($RootFile -cne 'one-line.pdf') { throw 'A file in the project folder must land at the Documents root.' }
function Assert-Refused([string]$Label, [string]$VaultFilePath) {
    try { $null = Get-VaultDocumentsRelativePath -ProjectPath $Project -VaultFilePath $VaultFilePath }
    catch { return }
    throw ('Accepted a Documents path that must be refused: ' + $Label)
}
Assert-Refused 'project folder' $Project
Assert-Refused 'duplicated project folder' ($Project + '/101097 - WM Wave 23 - Walmart Store 2151 1250kw/relay.zap15')
Assert-Refused 'different project' '$/Designs/Projects/101000-101999/101098 - Other/file.pdf'
Assert-Refused 'parent traversal' ($Project + '/../101098 - Other/file.pdf')
$Tokens = $null
$Errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'TestVaultProjectCopy.ps1'), [ref]$Tokens, [ref]$Errors)
if ($Errors.Count -ne 0) { throw ('Project copy script did not parse: ' + $Errors[0].ToString()) }
Write-Host 'PASS: Documents paths keep child folders and omit the Vault project folder.'
