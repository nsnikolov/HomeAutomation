# Restart the local HomeAutomation ASP.NET Core backend on TCP port 5080.
# Run from any directory: .\restart-backend.ps1
param(
    [ValidateRange(1, 65535)][int]$Port = 5080,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\backend\HomeAutomation.Api'
$logDir = Join-Path $root 'logs'
. (Join-Path $root 'startup-diagnostics.ps1')

if (-not (Test-Path (Join-Path $project 'HomeAutomation.Api.csproj'))) {
    throw "Cannot locate the HomeAutomation.Api project at: $project"
}

function Test-RepoBackend($process) {
    $binaryDirectory = (Join-Path $project 'bin').TrimEnd('\') + '\'
    $executable = [string]$process.ExecutablePath
    if ($process.Name -ieq 'HomeAutomation.Api.exe') {
        return $executable.StartsWith($binaryDirectory, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($process.Name -ine 'dotnet.exe') { return $false }
    if ([string]$process.CommandLine -notmatch '^\s*(?:"[^"]+"|\S+)\s+(?:"([^"]+)"|(\S+))') { return $false }
    $dll = $Matches[1]
    if (-not $dll) { $dll = $Matches[2] }
    if (-not [IO.Path]::IsPathRooted($dll) -or [IO.Path]::GetFileName($dll) -ine 'HomeAutomation.Api.dll') { return $false }
    return [IO.Path]::GetFullPath($dll).StartsWith($binaryDirectory, [StringComparison]::OrdinalIgnoreCase)
}

$owners = @(Get-BackendListenerIds $Port)
$verified = foreach ($ownerPid in $owners) {
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$ownerPid" -ErrorAction SilentlyContinue
    if (-not $proc -or -not (Test-RepoBackend $proc)) {
        throw "Port $Port is occupied by PID $ownerPid and ownership by this checkout could not be verified."
    }
    $target = Get-Process -Id $ownerPid
    if ($target.StartTime -ne $proc.CreationDate) {
        # CIM and Get-Process may report different timestamp precision.
        if ([Math]::Abs(($target.StartTime - $proc.CreationDate).TotalMilliseconds) -gt 1) {
            throw "Port $Port ownership changed during verification."
        }
    }
    $target
}
foreach ($target in $verified) {
    if ($target.HasExited) { continue }
    Write-Host "Stopping HomeAutomation backend PID $($target.Id)..."
    Stop-Process -InputObject $target -ErrorAction Stop
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while (@(Get-BackendListenerIds $Port).Count -gt 0) {
    if ((Get-Date) -ge $deadline) {
        throw "Port $Port did not become free after $TimeoutSeconds seconds."
    }
    Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$outLog = Join-Path $logDir 'backend.stdout.log'
$errLog = Join-Path $logDir 'backend.stderr.log'
Write-Host "Starting HomeAutomation backend from $project..."

# Start a separate process that survives the current PowerShell window.
# Set diagnostic flags with user-secrets, or in the host environment before launching.
$started = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', 'src/backend/HomeAutomation.Api') `
    -WorkingDirectory $root `
    -RedirectStandardOutput $outLog `
    -RedirectStandardError $errLog `
    -PassThru

# Uses the project's launch profile / configured Kestrel endpoints.
Write-Host "Started dotnet launcher PID $($started.Id)."
Write-Host "Logs: $outLog and $errLog"
Write-Host "Checking port $Port..."

$startupDeadline = (Get-Date).AddSeconds($TimeoutSeconds)
function Show-StartupFailure([string]$Reason) {
    Write-Warning $Reason
    foreach ($problem in (Get-BackendStartupProblems @(Get-BackendDiagnosticLines $logDir))) {
        Write-Warning $problem
    }
    Write-Warning "Startup logs: $outLog and $errLog"
}
while ((Get-Date) -lt $startupDeadline) {
    if ($started.HasExited) {
        $started.WaitForExit()
        Show-StartupFailure "dotnet launcher exited before startup (exit code $($started.ExitCode))."
        exit 1
    }
    $listeners = @(Get-BackendListenerIds $Port)
    if ($listeners.Count -gt 0) {
        foreach ($listenerId in $listeners) {
            $listener = Get-CimInstance Win32_Process -Filter "ProcessId=$listenerId" -ErrorAction SilentlyContinue
            if (-not $listener -or -not (Test-RepoBackend $listener) -or
                ($listenerId -ne $started.Id -and $listener.ParentProcessId -ne $started.Id)) {
                Show-StartupFailure "Port $Port has a listener that could not be matched to the new backend."
                exit 1
            }
        }
        Write-Host "Backend is listening on TCP port $Port."
        exit 0
    }
    Start-Sleep -Seconds 1
}

if ($started.HasExited) {
    $started.WaitForExit()
    Show-StartupFailure "dotnet launcher exited before startup (exit code $($started.ExitCode))."
} else {
    Show-StartupFailure "The backend did not listen on TCP port $Port within $TimeoutSeconds seconds."
}
exit 1
