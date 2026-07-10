$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
# Build on the same operating system and architecture as the destination server.
# PyInstaller bundles Python, so the server itself does not need Python installed.
python -m pip install -r requirements.txt pyinstaller
python -m PyInstaller --clean --onefile --name vault-to-procore vault_to_procore_sync.py
Write-Host "Built dist\vault-to-procore.exe"
