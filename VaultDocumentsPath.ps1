# The Vault project folder is the Procore project. Documents receives only the folders and files beneath it.
function Get-VaultDocumentsRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$VaultFilePath
    )
    $ProjectPath = $ProjectPath.Trim().TrimEnd('/')
    $VaultFilePath = $VaultFilePath.Trim().TrimEnd('/')
    if ($ProjectPath -notmatch '^\$/Designs/Projects/[^/]+/([^/]+)$') {
        throw 'Vault project path must be one project folder under a grouping folder.'
    }
    $ProjectFolder = $Matches[1]
    $Prefix = $ProjectPath + '/'
    if (-not $VaultFilePath.StartsWith($Prefix, [StringComparison]::Ordinal)) {
        throw 'File is not inside the selected Vault project folder.'
    }
    $Relative = $VaultFilePath.Substring($Prefix.Length)
    $Parts = @($Relative -split '/' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($Parts.Count -eq 0) {
        throw 'The Vault project folder is the Procore project and is not created in Documents.'
    }
    if ($Parts[0].Equals($ProjectFolder, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The Vault project folder is the Procore project and is not created in Documents.'
    }
    foreach ($Part in $Parts) {
        if ($Part -in @('.', '..') -or $Part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $Part.EndsWith('.') -or $Part.EndsWith(' ') -or
            $Part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)') {
            throw ('Cannot safely reproduce Windows name: ' + $Part)
        }
    }
    return ($Parts -join '/')
}
