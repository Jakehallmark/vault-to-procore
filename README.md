# Autodesk Vault to Procore Sync

Incrementally copies selected Autodesk Vault documents into matching Procore projects while preserving directory paths and document version history.

## Safety and behavior

- Procore projects are keyed by unique five- or six-digit project numbers.
- Vault files use their immutable Vault file ID as identity; paths are not identities.
- A SQLite ledger (`sync_state.db`) prevents unchanged Vault versions from being checked and uploaded repeatedly.
- A new Vault file creates a Procore document. A new Vault version appends a version to the existing Procore document.
- Conflicting or ambiguous matches are logged and skipped.
- The connector never deletes Procore documents.
- Dry-run is the default. Uploads require the explicit `--live` flag.

## Setup

1. Create a virtual environment and install `requirements.txt`.
2. Copy `.env.example` to `.env`.
3. Fill in the base URLs, company ID, approved routes, and access tokens when credentials are available.
4. Run `python vault_to_procore_sync.py --dry-run --verbose`.
5. After validating the plan, run `python vault_to_procore_sync.py --live`.

`--live` also requires `API_CONTRACT_CONFIRMED=true`. Keep this safety gate disabled until the exact Vault Data API response contract and Procore upload/folder/version workflow have passed integration tests in a non-production project.

Useful operational commands:

```text
vault-to-procore --validate-config
vault-to-procore --validate-config --live
vault-to-procore --status
vault-to-procore --dry-run --verbose
vault-to-procore --live
vault-to-procore --live --service
```

Only use `--service` when a continuously running process is required. Scheduled one-shot execution is the recommended deployment model.

## Unattended deployment

Build on the same operating system and CPU architecture as the target server. The PyInstaller output contains Python and application dependencies, so the target machine does not need a separate Python installation.

### Windows

1. Run `deployment\build.ps1` on Windows.
2. Run `deployment\windows\install-task.ps1` from an elevated PowerShell session.
3. Edit `C:\ProgramData\VaultToProcore\.env` and validate it.
4. Manually start the `VaultToProcoreSync` scheduled task for the first controlled run.

The scheduled task runs every 15 minutes by default, prevents overlapping instances, retries failures, and starts missed runs after a reboot.

### Linux

1. Run `deployment/build.sh` on a compatible Linux build machine.
2. Run `deployment/linux/install.sh` as root.
3. Edit `/etc/vault-to-procore/vault-to-procore.env`.
4. Validate with `/opt/vault-to-procore/vault-to-procore --validate-config --live` after loading the environment file.
5. Start with `systemctl enable --now vault-to-procore.timer`.

The service is hardened, runs under a dedicated unprivileged account, and is activated by a persistent systemd timer.

## Operations

The application uses a cross-platform PID lock, rotating logs, bounded exponential retry for transient HTTP failures, SQLite audit events, and a status command. Credentials remain external to the executable. Restrict the configuration file to the service identity on either platform.

The HTTP gateway routes and payload parsing are isolated from the sync engine. Confirm them against the exact Vault Data API deployment and approved Procore API application before the first live run.

The current gateway is an integration scaffold, not a certified live Procore upload implementation. Procore's supported workflow first creates an upload, sends bytes to the returned storage URL and fields, and then uses the upload UUID to create `/rest/v1.0/files` or `/rest/v1.0/file_versions`. Recursive folder lookup/creation and unattended OAuth token refresh must also be finalized when the API application and Vault deployment details are available.

## Version metadata

Every upload supplies source metadata including the Vault file ID, Vault version ID and number, source path, SHA-256 checksum, synchronization timestamp, and a version comment. The final representation of this metadata depends on the enabled Procore API fields; the local SQLite ledger always retains the authoritative cross-system mapping.
