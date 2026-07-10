$ErrorActionPreference = "Stop"
# Removing the task is intentionally separate from deleting data. An uninstall
# should not quietly remove the audit ledger, logs, or credentials.
Unregister-ScheduledTask -TaskName "VaultToProcoreSync" -Confirm:$false -ErrorAction SilentlyContinue
Write-Host "Task removed. Configuration, logs, and state were retained under ProgramData."
