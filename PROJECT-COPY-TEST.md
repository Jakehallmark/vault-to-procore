# Single-project download test

Create `TestVaultProjectCopy.ps1` beside the working connection script by copying its source into VS Code on the work laptop.

Inside the successful-login `try` block in `VaultConnectionCheck.ps1`, replace the existing direct call to `ScanVaultProjects.ps1` with:

```powershell
& (Join-Path $PSScriptRoot 'TestVaultProjectCopy.ps1') -Connection $Connection
```

Keep this call after `$Connection = $Result.Connection` and before the `finally` block that logs out. Keep the working authentication setup. If using the development copy's `if ($ScanProjects)` block, replace that whole block with the line above instead.

Run from the project directory in the work laptop's VS Code terminal:

```powershell
powershell.exe -NoProfile -File .\VaultConnectionCheck.ps1
```

Enter the exact project folder path when prompted. This test accepts a project directly under a grouping folder such as `$/Designs/Projects/103000-103999/103821 - Project Name`; replace that example with a real full path. Review the printed list and the JSON report, then type `DOWNLOAD` to approve the batch or press Enter to cancel. All selected extensions are included under the chosen project, including any Archive or Obsolete subfolders; no folder exclusions have been agreed for this test.

Output is a fresh run under `%TEMP%\VaultTransfer-Test`. Its `files` directory contains the project folder and the relative folders containing successful downloads. Empty source folders and folders containing only excluded file types are omitted. `download-cache` is separate diagnostic space and may contain empty directories or partial files following an error. `transfer-report.json` records each selected Vault file ID/version, destination, result, error, and local SHA-256. SHA-256 is a local fingerprint, not a comparison against a server hash.

Files are the latest versions found during this project's scan, pinned by the returned file records for acquisition. The test does not follow Vault dependency links outside the project, check out files, or upload to Procore. Inaccessible/version-zero records are excluded and counted. The scope is the files returned to the current account; inaccessible records mean this is not a complete copy of every project file.

The implementation uses Autodesk's VDF `AcquireFiles` download mechanism, with an explicit local folder per file. See [Autodesk download guidance](https://help.autodesk.com/cloudhelp/2026/ENU/Vault-DevTools/files/GUID-C711E51B-B069-4A80-8611-0A6FC8B5FA13.htm) and [Autodesk's explanation of local download paths](https://forums.autodesk.com/t5/vault-customization-forum/acquire-file-to-specific-folder-and-maintain-folder-structure/td-p/5133780). The installed Vault 2024 SDK still requires a live download test. Local checks cover PowerShell parsing and filename validation only.
