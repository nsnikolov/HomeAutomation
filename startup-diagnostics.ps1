function Get-BackendListenerIds([int]$Port) {
    # netstat supplies listener PIDs without requiring access to another process's command line.
    @(netstat.exe -ano -p tcp | ForEach-Object {
        if ($_ -match "^\s*TCP\s+\S+:${Port}\s+\S+\s+LISTENING\s+(\d+)\s*$") { [int]$Matches[1] }
    } | Sort-Object -Unique)
}

function Get-BackendDiagnosticLines([string]$LogDirectory) {
    foreach ($name in 'backend.stdout.log', 'backend.stderr.log') {
        $path = Join-Path $LogDirectory $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Get-Content -LiteralPath $path -Encoding UTF8 -Tail 300 -ErrorAction SilentlyContinue
        }
    }
}

function Get-ProviderStartupSummary([string[]]$Lines, [int]$ProcessId, [datetime]$StartedUtc) {
    $summaries = @{}
    foreach ($line in $Lines) {
        if ($line -notmatch '"EventId"\s*:\s*6100\b') { continue }
        try {
            $entry = ConvertFrom-Json -InputObject $line -ErrorAction Stop
            $state = $entry.State
            if ($entry.Category -cne 'HomeAutomation.Api' -or $state.ProcessId -ne $ProcessId -or
                -not $state.ObservedAtUtc -or ([DateTimeOffset]$state.ObservedAtUtc).UtcDateTime -lt $StartedUtc) { continue }
            if ($state.Provider -cnotin @('growatt', 'daikin', 'ewelink', 'tuya') -or
                $state.Source -cnotin @('Legacy', 'Vault', 'Unknown') -or
                $state.ValidationStatus -cnotin @('Valid', 'Invalid', 'Disabled', 'NotConfiguredOrInvalid')) { continue }
            $scope = switch -CaseSensitive ($state.Scope) {
                'Global' { if ($null -eq $state.PropertyId) { 'Global' } }
                'Property' { if ($state.PropertyId -match '^[1-9][0-9]{0,18}$') { "Property $($state.PropertyId)" } }
                'Unknown' { 'Unknown' }
            }
            if ($scope) {
                $summaries[$state.Provider] = [pscustomobject]@{
                    Source = $state.Source; Scope = $scope; Status = $state.ValidationStatus
                }
            }
        } catch { continue }
    }
    $summaries
}

function Get-BackendStartupProblems([string[]]$Lines) {
    # Emit fixed descriptions only. Compiler diagnostics and exception messages can contain secrets.
    $problems = foreach ($line in $Lines) {
        switch -Regex ($line) {
            'address already in use|failed to bind to address|AddressInUseException|WSAEADDRINUSE' { 'Port binding failed.'; break }
            '\berror\s+((?:CS|MSB|NETSDK|NU)\d{4,5})\b' { "Build failed ($($Matches[1]))."; break }
            'build failed|failed to build' { 'Build failed.'; break }
            'missing imported operational settings|provider validation failed|invalid stored operational settings|vault key or encrypted secret|provider identity differs|convert legacy identifiers' { 'A selected Vault profile is missing, invalid or unreadable.'; break }
            'missing required fields|invalid database bootstrap configuration|OptionsValidationException|Set Database:' { 'Required configuration is missing or invalid.'; break }
            'apply migration 016' { 'The Vault migration ledger is incomplete.'; break }
            'configuration failed' { 'Provider configuration validation failed.'; break }
            'Unhandled exception|"LogLevel"\s*:\s*"(?:Error|Critical)"' { 'The backend logged an error. Inspect the protected log files locally.'; break }
        }
    }
    $problems | Select-Object -Unique
}
