# Run while Procore Drive's project dropdown is open, with its search box empty.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$Processes = @(Get-Process | Where-Object { $_.MainWindowTitle -like '*Procore Drive*' })
if ($Processes.Count -ne 1) {
    throw 'Open one Procore Drive window, then retry.'
}
$ProcessId = $Processes[0].Id
Write-Host 'Within 8 seconds, switch to Procore Drive and open the project dropdown.'
Start-Sleep -Seconds 8
$Desktop = [System.Windows.Automation.AutomationElement]::RootElement
$Condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$Windows = $Desktop.FindAll([System.Windows.Automation.TreeScope]::Children, $Condition)
$Rows = New-Object 'System.Collections.Generic.List[object]'
foreach ($Window in $Windows) {
    $Elements = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($Element in $Elements) {
        try {
            $Current = $Element.Current
            if ([string]::IsNullOrWhiteSpace($Current.Name)) { continue }
            $Rows.Add([pscustomobject]@{
                Name = $Current.Name
                ControlType = $Current.ControlType.ProgrammaticName
                AutomationId = $Current.AutomationId
                Offscreen = $Current.IsOffscreen
            })
        }
        catch [System.Windows.Automation.ElementNotAvailableException] { continue }
    }
}
$Output = Join-Path $PSScriptRoot 'procore-ui-check.json'
ConvertTo-Json -InputObject $Rows.ToArray() -Depth 3 |
    Set-Content -LiteralPath $Output -Encoding UTF8
Write-Host ('Saved ' + $Rows.Count + ' named controls to ' + $Output)
Write-Host 'Diagnostic snapshot only: this does not establish a complete project inventory.'
