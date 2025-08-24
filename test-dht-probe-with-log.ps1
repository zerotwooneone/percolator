# Wrapper script to run test-dht-probe.ps1 and capture full logs to a timestamped file
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

# Ensure logs directory exists
$logDir = Join-Path $PSScriptRoot 'logs'
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir | Out-Null
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $logDir "test-dht-probe-$timestamp.log"

# Start transcript to capture everything (stdout, stderr, verbose, debug)
Start-Transcript -Path $logPath -IncludeInvocationHeader | Out-Null

$exitCode = 0
try {
    # Forward all incoming arguments to the underlying script
    & (Join-Path $PSScriptRoot 'test-dht-probe.ps1') @args
    if ($LASTEXITCODE) { $exitCode = $LASTEXITCODE }
}
catch {
    Write-Error $_
    $exitCode = 1
}
finally {
    Stop-Transcript | Out-Null
}

Write-Host "Log saved to: $logPath"
exit $exitCode
