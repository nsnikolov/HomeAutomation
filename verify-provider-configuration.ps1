param(
    [ValidateRange(1, 65535)][int]$Port = 5080,
    [string]$LogDirectory = (Join-Path $PSScriptRoot 'logs')
)

. (Join-Path $PSScriptRoot 'startup-diagnostics.ps1')
$summaries = @{}
$healthy = $false
$listeners = @(Get-BackendListenerIds $Port)
if ($listeners.Count -eq 1) {
    try {
        $listener = Get-Process -Id $listeners[0] -ErrorAction Stop
        $health = Invoke-WebRequest -Uri "http://localhost:$Port/health/live" -UseBasicParsing -TimeoutSec 3 -MaximumRedirection 0 -ErrorAction Stop
        $healthy = $health.StatusCode -eq 200 -and $health.Content -eq 'Healthy'
        if ($healthy -and $listener.StartTime) {
            $summaries = Get-ProviderStartupSummary @(Get-BackendDiagnosticLines $LogDirectory) $listener.Id $listener.StartTime.ToUniversalTime()
        }
    } catch { }
}
if ($listeners.Count -eq 0) { Write-Warning "No TCP listener was found on port $Port." }
elseif (-not $healthy) { Write-Warning 'Local backend liveness could not be attributed to a single listener.' }
elseif ($summaries.Count -eq 0) { Write-Warning 'No startup summary could be matched to the current healthy listener.' }

$failed = $false
$unknown = $false
foreach ($provider in 'growatt', 'daikin', 'ewelink', 'tuya') {
    $entry = $summaries[$provider]
    if (-not $entry) {
        Write-Output "$provider : Unknown (no current startup confirmation)"
        $unknown = $true
        continue
    }
    Write-Output "$provider : $($entry.Source), $($entry.Scope), $($entry.Status)"
    if ($entry.Status -notin @('Valid', 'Disabled')) { $failed = $true }
    if ($entry.Source -eq 'Unknown' -or $entry.Scope -eq 'Unknown') { $unknown = $true }
}
if ($unknown -or $failed) {
    if ($summaries.Count -eq 0) {
        foreach ($problem in (Get-BackendStartupProblems @(Get-BackendDiagnosticLines $LogDirectory))) {
            Write-Warning "Uncorrelated log evidence: $problem"
        }
    }
}
if ($listeners.Count -eq 0 -or $failed) { exit 1 }
if ($unknown) { exit 2 }
exit 0
