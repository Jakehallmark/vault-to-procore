#!/bin/sh
set -eu
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$ROOT"
# PyInstaller creates a native Linux artifact. Build this on a machine compatible
# with the target server rather than copying the Windows executable across.
python3 -m pip install -r requirements.txt pyinstaller
python3 -m PyInstaller --clean --onefile --name vault-to-procore vault_to_procore_sync.py
echo "Built dist/vault-to-procore"
