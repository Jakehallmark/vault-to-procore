# Work laptop: live proof of concept

## Current step: run the tray app

Pull this repo and extract the committed build. Do not run `Build.ps1` on this laptop.

```powershell
New-Item -ItemType Directory -Force dist\win-x64
tar -xf release\VaultTransfer-win-x64.tar.xz -C dist\win-x64
dist\win-x64\VaultTransfer.exe
```

Sign in to Vault Professional first if you have not already; the app uses that saved server, database, and Windows account. Choose Scan now. The first Procore scan opens a browser. Later scans, including the scheduled ones, sign in to both without asking. Closing the window leaves it in the system tray. The app writes projects, file versions, and changes to `%LocalAppData%\VaultTransfer\catalog\catalog.json`. It does not upload files.

## Previous step: inventory the real project tree

The Windows-authenticated connection and root-folder query succeeded on the work laptop (user confirmed). The screenshot shows the source root as `$/Designs/Projects`.

1. Create `ScanVaultProjects.ps1` next to the working `VaultConnectionCheck.ps1`, using the new file's complete contents.
2. In the working connection script, add this call after the successful root-folder query and its success messages, **inside the `try` block and before its closing brace followed by `finally`**:

```powershell
& (Join-Path $PSScriptRoot 'ScanVaultProjects.ps1') -Connection $Connection
```

3. Run the working connection script as before. Stay connected to the VPN. Do not invoke the inventory after logout; it needs the live connection passed above.

The development copy alternatively includes a `-ScanProjects` switch to enable that call. Use either the direct call or the switch-controlled call, not both.

Reports go into a unique subfolder under `reports` beside the scripts:

- `inventory.csv`: human-readable table, with text prefixes for safe spreadsheet display.
- `inventory.jsonl`: exact metadata, one JSON file record per line, for later app import.
- `scan-summary.json`: root, start/end times, counts, enumeration status, and any error.

The scan uses `DocumentService.GetFolderByPath`, `GetLatestFilesByFolderId`, and `GetFoldersByParentId`. It includes hidden file records returned to this account and walks one folder at a time. It records the latest file versions returned during the scan; it is not a historical-version inventory or an atomic snapshot. A server or permission error stops the scan and leaves `.partial` report filenames. An inaccessible branch hidden entirely by Vault cannot be inventoried.

Every file remains pending. No documents are downloaded or uploaded. Project matching, approval in the C# window, and Procore transfers are not implemented by this inventory script. The `100000-100999` style folders shown in the screenshot are range groupings; they must not be treated as actual project numbers. The scan preserves those paths without guessing project mappings.

Only syntax has been checked locally. Confirm the live file/folder counts or report the error after the work-laptop run; company filenames do not need to be shared here.

## Next step: check the real Vault connection

Reported installation: `C:\Program Files\Autodesk\Vault Client 2024\Explorer`. Procore Drive version: **3.0.7**. Windows authentication and the read-only root query are now confirmed by the work-laptop run; VPN access is required. The instructions below describe the already completed connection check.

Leave the C# files already entered as they are for now. Create `VaultConnectionCheck.ps1` from the supplied source in your project folder. With the VPN connected, run it in a fresh 64-bit Windows PowerShell 5.1 process:

```powershell
powershell.exe -NoProfile -File .\VaultConnectionCheck.ps1
```

Enter the **Server** and **Vault** database names from the Vault login dialog when prompted. They remain on the work laptop; the script does not ask for a password. It checks the installed libraries, attempts a separate read-only Windows-authenticated SDK connection, queries the actual Vault root, and signs out of that separate connection. It does not reuse or sign out the running Vault client's session.

Expected success: `SUCCESS: Windows login accepted; read-only Vault root query returned folder ID ...`. If it fails, retain the exact error. Do not change execution policies or credentials to guess around an error. This step does not yet test Procore Drive or transfer any document.

The script has been syntax-checked, and its dependency-loading helper compiles with the Windows .NET Framework compiler. Full Windows PowerShell 5.1 execution was not verified here because the local script policy requires signed scripts. Actual Vault login and the root query must be tested on the work laptop because the development computer does not have this Vault installation or its server connection. If the work laptop also requires signed scripts, report that error so the connection check can use the permitted C# workflow instead.

## Pause at LoadSample

The earlier local demonstration is withdrawn. Do not enter `LoadSample`, `LoadPreview`, or the sample-loading button. Do not use the old sample-run instructions.

Keep the source already entered. The real Vault and Procore Drive connections require installation-specific corrections before the app can function as requested. The current code is not a live proof of concept merely because it builds.

Changes already made in the development copy:

- Removed the `Load sample folders` button from `BuildSettings`.
- Removed the complete `LoadSample` and `LoadPreview` methods from `MainForm.cs`.
- Removed the preview branch from `Program.cs`.
- Removed local test mode from the application's selection list.
- Prevented previous local demonstration settings/reviews from entering the intended live workflow.

No replacement demonstration has been added. Internal copy-engine tests remain separate from normal app use.

## Establish the real connections first

On the work laptop, record:

1. The folder containing the Vault Professional 2024 executable. Use the shortcut's **Open file location** or Task Manager's **Open file location**.
2. The installed Procore Drive version, using its About information or executable file properties.
3. Whether Vault uses Windows authentication, an Autodesk ID, or a Vault username/password. Do not share passwords or tokens.

The live implementation must use version-compatible Vault SDK libraries to establish the user's connection, list files with version identities, and retrieve approved versions. Procore Drive automation must verify the selected company/project/folder and confirm the completed upload. Neither connection has yet been verified from the personal development computer.

## Acceptance sequence

- Connect to the actual Vault environment.
- Scan a configured real folder and display a review report without downloading files.
- Approve one real file and deny another eligible file.
- Download only the approved version into the local staging folder.
- Have the app upload it through Procore Drive and verify the resulting destination file.
- Confirm the denied file did not transfer, then expand the scan scope.

No task installation, service, ZIP handoff, or sample dataset is required. Continue entering corrected source file by file after the connection details are established.
