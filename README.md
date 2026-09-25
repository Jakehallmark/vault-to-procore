# Vault Transfer

A manually launched Windows app intended to scan Autodesk Vault Professional 2024, apply company rules, present a transfer report for approval or denial, stage approved file versions locally, and automatically upload them through the installed Procore Drive application.

## Current status

**The separate Vault connection check succeeded on the work laptop. The C# app and Procore Drive connection are not yet a complete live proof of concept.** The previous local demonstration did not meet the required workflow. Its sample generator, sample button, preview command, and user-selectable local test mode have been removed. Old demonstration reviews are not accepted by the app.

The work-laptop inventory completed with 159,675 records across 14,997 folders. The single-project download test then copied all three selected files, and the user confirmed the local files and preserved folder structure. This verifies the separate PowerShell workflow, not yet the C# app integration.

## Next milestone: Procore project inventory and matching

`ImportProjectInventory.ps1` now imports the Portfolio CSV and the Vault JSON-lines inventory into a versioned local JSON snapshot under `reports`. It retains source records, source hashes, import time, exact-number matches, missing numbers, and duplicate-number exceptions. The supplied export contains 568 rows and yields 304 unambiguous number matches. All 568 Procore rows were verified to appear exactly once in the comparison. Project IDs and active status remain unknown because this CSV does not supply them; Stage is not treated as active status.

Run on the work laptop using actual source file paths:

```powershell
powershell.exe -NoProfile -File .\ImportProjectInventory.ps1 -ProcoreCsv .\projects.csv -VaultInventory .\inventory.jsonl -CompanyName 'PowerSecure, Inc.'
```

Use the actual Vault filename (`inventory.json1` if its extension is a digit 1). Each import preserves previous snapshots. This is a working manual-import helper, not yet integrated into the C# UI. Automatic API refresh still requires Procore app registration, company installation, and OAuth setup. The CSV snapshot is not asserted to be a complete company inventory.

Uploads are deferred. First retrieve the projects visible to the user's account in the chosen Procore company and produce a read-only comparison with the Vault inventory.

- Retain Procore company and project identifiers where available, project name, project number, and active/inactive status where available.
- Match exact company project numbers; preserve original values and leading zeros. Any extraction from project names must use a confirmed naming rule.
- Report unique exact matches, Vault-only projects, Procore-only projects, and ambiguous/missing-number entries requiring review. Duplicate project numbers must not be automatically resolved by name similarity.
- Retain the full Vault project path because more than one folder can share a project number.
- Record enumeration completeness and filters. An incomplete list cannot establish that a project is absent.
- Matching does not approve any files or trigger downloads/uploads.

Procore inventory access remains unverified. The public API requires its own authorized integration; an existing Drive login is not proof of API access. Inspect the work laptop's Drive project selector before choosing how the app will read its project list. Do not assume a visible subset of a scrolling list is the complete inventory.

The C# settings, review controls, saved-report handling, and local file-copy primitives remain as development code. Their isolated tests do not prove Vault access or Procore uploads. The current app blocks scanning until the real connections are implemented; there is no automatic fallback to local folders.

## Initial file-type selection

Initial configurable allowlist: `.pdf`, `.rdb`, `.sup`, `.urs`, `.usw`, `.wset`,
`.zap14`, `.zap15`, `.zap15_1`, `.zap16`, `.zap17`, `.zap18`, `.s7s`, `.cd3`, `.cd31`, `.cd32`.
The supplied ZAP15 selection is interpreted to include the observed `.zap15_1` extension.
ZAP14 is included as confirmed by the user.
These are candidate types only; project/folder rules and individual approvals still apply.
Defaults apply to new settings; previously saved settings retain their existing extension list.

## Required first proof of concept

1. Establish an authenticated, supported connection to the installed Vault Professional 2024 environment under the user's own account.
2. Scan an actual configured Vault folder and show real file identities, versions, paths, and proposed project mappings without copying files.
3. Allow approval or denial of the real suggestions.
4. Retrieve the approved Vault versions into the configured local staging folder.
5. Automatically upload those staged files through Procore Drive into the approved company, project, and folder.
6. Confirm upload completion before reporting success. Preserve staging and record failures when the upload cannot be confirmed.f

The first controlled run must use real files. Broader recursive scanning follows verification of the actual connection and transfer workflow. No sample-data step is part of the product or handoff.

## Work-laptop details needed

- Vault Professional 2024 folder: `C:\Program Files\Autodesk\Vault Client 2024\Explorer` (reported). The installed SDK libraries still need verification.
- Windows authentication and the read-only root-folder query succeeded on the work laptop, as confirmed by the user. VPN access is required. Do not share passwords or tokens.
- Procore Drive version: **3.0.7** (reported). The upload controls still need inspection on that installation.
- Company project-number rules and approved real source/destination locations, configured on the work laptop.

A running signed-in desktop application does not by itself establish a reusable connection for a separate application. Verify that connection explicitly. Do not assume Procore Drive exposes a normal writable filesystem destination.

`VaultConnectionCheck.ps1` uses the installed Vault libraries under Windows PowerShell 5.1, establishes read-
only Windows authentication, and queries the real Vault root. The user has confirmed this succeeds on the work laptop. The development copy also accepts `-ScanProjects` to invoke the new inventory before logging out. The inventory uses the documented folder/file traversal approach: [Autodesk's file enumeration guidance](https://blog.autodesk.io/coding-for-performance-getting-all-files/).

## Manual execution

No scheduled task, service, startup entry, or installer is used. The current shell targets C# / Windows Forms / .NET 10. Its compatibility with the Vault 2024 SDK must be verified before implementing the live connection; a version-compatible helper may be required.

Development build:

```powershell
dotnet restore VaultTransfer.csproj --configfile NuGet.Config
dotnet build VaultTransfer.csproj --no-restore
```

Internal copy-engine tests are available through `--self-test`. They use temporary files and are not an application demonstration or a live integration test.

## Manual source entry

See [WORK-LAPTOP.md](WORK-LAPTOP.md). Pause entry at the former `LoadSample` method while the connection-specific corrections are being established. Existing code before that point can remain; it is not yet the final live implementation.

## References

- [Autodesk Vault API and SDK](https://aps.autodesk.com/developer/overview/vault)
- [Vault Professional 2024 installed components](https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Components-installed-by-Vault-Professional-2024-Client.html)
- [Procore Drive upload procedure](https://support.procore.com/products/procore-drive/documents/tutorials/upload-files-into-a-folder-in-procore-drive)
