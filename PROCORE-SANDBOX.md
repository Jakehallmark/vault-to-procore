# Procore sandbox connection and project inventory

The Vault Migration Tool is installed as a Data Connection app in the developer sandbox (company `4290241`, confirmed by the installation screenshot). It uses User Level Authentication. Live sandbox sign-in and retrieval of the two default projects succeeded on September 25, 2026. The initial helper incorrectly requested more pages; that behavior is now corrected. The corrected live run is verified: reports/procore-sandbox-889bd142ec6145ce81e00d4c9e712328/projects.json contains two projects, one response, EnumerationFinished=true, and Error=null. These are Sandbox Test Project (368375) and Standard Project Template (368376). Project numbers and active status were not supplied; both remain unknown.

## Run on your computer

Keep `ConnectProcoreSandbox.ps1` and `ProcoreSandbox.Core.ps1` together. In the developer portal's **OAuth Credentials > Sandbox OAuth Credentials**, leave the Redirect URI exactly `http://localhost`.

From the repository folder in a normal PowerShell terminal:

```powershell
pwsh -NoProfile -File .\ConnectProcoreSandbox.ps1
```

1. Enter the **sandbox Client ID** when prompted.
2. Copy the **sandbox Client Secret** from the portal and paste it into the hidden prompt. Do not put it in a command, source file, or chat.
3. The default browser opens the sandbox sign-in page. Sign in with your sandbox account and authorize the app.
4. The browser redirects to `http://localhost/?code=...&state=...`. It may display a connection error because this helper does not run a local web server. Copy the **complete address from the address bar** and paste it into the helper's hidden prompt. Do not share that address. Complete this within ten minutes of opening sign-in.
5. The helper verifies the callback and signed-in user, reads the company project list, then prints the report location. Close the callback browser tab afterward.

Use the secret input in an ordinary local terminal; do not run while recording a debugging session. Credentials and tokens are used in process memory only, not intentionally persisted; .NET strings cannot be reliably erased. No token cache or automatic refresh is implemented, so each run requires a new sign-in.

PowerShell 7 (`pwsh`) was used for local offline validation. The scripts also target Windows PowerShell 5.1 syntax, but local execution under `powershell.exe` was blocked by its signed-script policy, so that runtime remains unverified. If script execution is blocked by your computer's policy, retain the policy error and use your organization's permitted execution method. This helper does not change policy or require administrator privileges.

For a different developer sandbox, pass its numeric company ID explicitly:

```powershell
pwsh -NoProfile -File .\ConnectProcoreSandbox.ps1 -CompanyId YOUR_NUMERIC_SANDBOX_COMPANY_ID
```

## What gets saved

A unique `reports/procore-sandbox-<run>/projects.json` contains company/user IDs, project IDs, exact project numbers, names, and active status when provided. Missing number/status values remain null. The report records the endpoint, filters, times, page count, and enumeration status. Reports remain excluded from Git.

The company-projects endpoint documents a single list response with no pagination parameters. The helper requests that list once. A repeated ID, malformed response, or HTTP failure stops the query and saves `projects.partial.json`. Authentication failures do not create inventory reports. Never treat a partial report as a complete list.

`EnumerationFinished` means the single company-projects response was successfully received and validated. `CompleteCompanyInventoryVerified` remains false: permissions and endpoint defaults can limit results, and this report is not an atomic company-wide snapshot. No explicit active/inactive filter is sent. This is developer sandbox data, not the company's production inventory, and it must not replace the existing CSV comparison or be used to approve transfers.

The only remote operations are OAuth authorization/token exchange and GET requests for the current user and company projects. There are no document downloads, uploads, project changes, or automatic matching. API redirects are rejected; the helper cannot target production hosts. HTTP errors stop immediately without automatic retries and omit response bodies.

## Validation and next step

Run `pwsh -NoProfile -File .\TestProcoreSandbox.ps1` for offline checks of callback validation, encoded parameters, authorization/company headers, single-response enumeration, empty results, malformed responses, and failure handling. These checks do not establish live Procore access.

After the live run, share only the project count and whether enumeration finished, or the helper's sanitized error. Keep credentials and callback URLs local. Production installation, authorization, and inventory remain separate next steps after the sandbox connection succeeds.

## Official references

- [Sandbox hosts and environment separation](https://procore.github.io/documentation/development-environments)
- [Authorization and token parameters](https://procore.github.io/documentation/oauth-endpoints)
- [Company projects API](https://developers.procore.com/reference/rest/company-projects)
- [Installation and user-level permissions](https://procore.github.io/documentation/building-apps-install-arch)
