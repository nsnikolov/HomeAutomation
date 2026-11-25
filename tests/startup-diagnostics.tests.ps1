$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'startup-diagnostics.ps1')

$now = [datetime]::UtcNow
$sample = @{
    EventId = 6100; Category = 'HomeAutomation.Api'; Message = 'fixture-secret-must-not-be-printed'
    State = @{ Provider = 'growatt'; Source = 'Vault'; Scope = 'Property'; PropertyId = 3
        ValidationStatus = 'Valid'; ProcessId = 123; ObservedAtUtc = $now.ToString('O') }
}
$current = ConvertTo-Json $sample -Compress
$sample.State.ObservedAtUtc = $now.AddDays(-1).ToString('O')
$old = ConvertTo-Json $sample -Compress
$summary = Get-ProviderStartupSummary @($current, $old, '{"EventId":6100') 123 $now
if ($summary.Count -ne 1 -or $summary.growatt.Source -ne 'Vault' -or $summary.growatt.Scope -ne 'Property 3') {
    throw 'Current startup event was not recognized.'
}
if ((Get-ProviderStartupSummary @($old) 123 $now).Count -ne 0 -or
    (Get-ProviderStartupSummary @($current) 456 $now).Count -ne 0) {
    throw 'Stale or unrelated logs were accepted.'
}
$problems = Get-BackendStartupProblems @(
    'error CS1001 fixture-secret', 'Address already in use fixture-secret',
    'missing required fields fixture-secret', 'missing imported operational settings fixture-secret')
if ($problems.Count -ne 4 -or ($problems -join ' ') -match 'fixture-secret') { throw 'Unsafe or incomplete startup diagnostics.' }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("ha-startup-" + [guid]::NewGuid())
New-Item -Type Directory -Path (Join-Path $sandbox 'src/backend/HomeAutomation.Api') -Force | Out-Null
try {
    New-Item -Type File -Path (Join-Path $sandbox 'src/backend/HomeAutomation.Api/HomeAutomation.Api.csproj') | Out-Null
    Copy-Item -LiteralPath @((Join-Path $root 'restart-backend.ps1'), (Join-Path $root 'startup-diagnostics.ps1'),
        (Join-Path $root 'verify-provider-configuration.ps1')) -Destination $sandbox
    function netstat.exe { }
    function Start-Process {
        param($FilePath, $ArgumentList, $WorkingDirectory, $RedirectStandardOutput, $RedirectStandardError, [switch]$PassThru)
        Set-Content -LiteralPath $RedirectStandardError 'error CS1001 fixture-secret'
        $fake = [pscustomobject]@{ Id = 123; HasExited = $true; ExitCode = 17 }
        $fake | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { }
        $fake
    }
    $failure = (& (Join-Path $sandbox 'restart-backend.ps1') 3>&1 | Out-String)
    if ($LASTEXITCODE -ne 1 -or $failure -notmatch 'exit code 17' -or $failure -notmatch 'Build failed \(CS1001\)' -or
        $failure -match 'fixture-secret') { throw 'Restart failure reporting did not pass.' }
    function netstat.exe { ' TCP 127.0.0.1:5080 0.0.0.0:0 LISTENING 123' }
    function Get-Process { param($Id) [pscustomobject]@{ Id = 123; StartTime = $now } }
    function Invoke-WebRequest { param($Uri, [switch]$UseBasicParsing, $TimeoutSec, $MaximumRedirection)
        [pscustomobject]@{ StatusCode = 200; Content = 'Healthy' } }
    $unknown = (& (Join-Path $sandbox 'verify-provider-configuration.ps1') 3>&1 | Out-String)
    if ($LASTEXITCODE -ne 2 -or ($unknown | Select-String 'Unknown \(no current startup confirmation\)' -AllMatches).Matches.Count -ne 4) {
        throw 'Missing startup evidence was not reported as unknown.'
    }
} finally {
    if ((Resolve-Path -LiteralPath $sandbox).Path -eq [IO.Path]::GetFullPath($sandbox)) {
        Remove-Item -LiteralPath $sandbox -Recurse
    }
}
'Startup diagnostics fixtures passed.'
