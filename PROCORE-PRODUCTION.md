# PowerSecure production inventory

Vault Migration Tool 0.1.0 is installed in company **12233**. Production sign-in and the 932-project list are verified. The detail query returned 593 project records before rate limiting. The original partial report remains available for resuming.

## Resume the current run

```powershell
pwsh -NoProfile -File .\ConnectProcoreProduction.ps1 -ResumeReport .\reports\procore-production-228c285ed3d44a34a794ea8e6e7616b3\projects.partial.json
```

Use PowerShell 7. Keep the production Redirect URI in the developer portal exactly `http://localhost`.

The script reads the local `.secrets` file, opens your browser, and receives the callback automatically. Sign in and authorize; you no longer copy an address. Temporary listeners bind only to IPv4/IPv6 loopback on port 80 and close after success, denial, timeout, or failure. No Windows service, firewall rule, or URL reservation is installed. If another program owns port 80, the helper stops before opening sign-in; close that program or explicitly use `-ManualCallback` for the old hidden-paste flow.

The `.secrets` format is:

```text
#Production
Client ID: your-production-client-id
Client Secret: your-production-client-secret
```

This plaintext local file is ignored by Git. Values are never printed. Without it, local credential prompts remain available. `-SecretsFile <path>` selects another file. Access tokens stay in memory. After the first browser sign-in, the refresh token is stored with Windows DPAPI for the current user at `%LocalAppData%\VaultTransfer\procore.refresh`. Reports and cooldown files still contain no credentials. A new refresh token replaces the saved one as soon as Procore issues it.

## Pacing and recovery

All production data GETs use one sequential request controller. It reads `X-Rate-Limit-Remaining`, `X-Rate-Limit-Reset`, `Retry-After`, and server Date headers. Requests are paced from the current budget with a five-request reserve and a 2.5-second minimum interval. The headers may describe the short spike window or hourly window; no fixed hourly allowance is assumed.

A 429 pauses until the later applicable reset/retry time, with a small margin and jitter. Retry-After seconds and HTTP-date forms are supported. Temporary network failures and HTTP 408/500/502/503/504 use bounded backoff. Each GET permits at most five retries; permanent errors such as 400/403/404 stop immediately. A future cooldown is saved without credentials so restarting the helper does not immediately repeat throttled requests. Only one production run per company can run from this repository at a time.

Long waits sleep in interruptible one-second increments and display the resume time. Ctrl+C stops the script; completed project checkpoints remain available. Access tokens refresh in memory before expiry or once after a 401. Token POSTs are never blindly replayed after an uncertain response: a failed refresh ends the run safely and may require a fresh sign-in. Automatic retries apply only to the permitted read-only API calls, not future uploads.

Resume verifies the same company and signed-in user, project IDs, completion timestamps, and counts. Completed metadata is skipped. The original list and timestamps are retained; it is not a fresh company scan. A new run directory records the source checkpoint hash and resume time. The original report is never overwritten.

## Fresh inventory and comparison

```powershell
pwsh -NoProfile -File .\ConnectProcoreProduction.ps1 -IncludeDetails
```

The helper requests the company-projects list once, then GETs `/rest/v1.0/projects/{id}?company_id=12233` with the matching company header. It saves an atomic checkpoint after each successful detail. Total duration depends on the rate-limit budget, not just project count.

After all details succeed, `CompareProcoreMetadata.ps1` automatically compares exact project numbers with the full Vault paths retained by the saved PowerSecure CSV/Vault snapshot. If there is more than one `reports/project-inventory-*/projects.json`, use `-VaultSnapshot <path>`. The raw Vault JSONL is not present locally, so this is explicitly a historical snapshot comparison, not a fresh scan.

Duplicate/missing numbers and multiple Vault folders require review. Original strings and leading zeros are preserved. No numbers are inferred from names. Every API project is retained once. Snapshot timestamps/hashes and incomplete company-access scope remain explicit. Number matching does not approve transfers.

New results are written under `reports/procore-production-<run>` and `reports/production-vault-comparison-<run>`. Incomplete detail runs retain `projects.partial.json` and cannot trigger comparison. No project/document writes or uploads are implemented.

## Verification and UI requirements

Offline suites: `TestProcoreSandbox.ps1`, `TestProcoreComparison.ps1`, and `TestProcoreRuntime.ps1`. The runtime suite uses fake HTTP responses and real localhost sockets with fake callback data, without accessing Procore. Automatic live callback, header-driven pacing, and refresh still require live acceptance testing.

The future UI must use this same single request queue and checkpoint model, show progress and waiting/reset times, offer cancellation/resume, and reuse saved metadata rather than fetch every project on every launch. Cross-machine coordination is not implemented by the local lock. Bulk metadata endpoints/incremental refresh should be evaluated before expanding usage. Upload retry behavior will require its own idempotency and destination verification design; it must not reuse GET retry semantics.

Official references: [Rate limiting](https://procore.github.io/documentation/rate-limiting), [OAuth and refresh](https://procore.github.io/documentation/oauth-endpoints).