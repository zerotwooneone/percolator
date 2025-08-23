param(
    [int]$Port = 5000,
    [string]$HostIdentity = 'host',
    [string]$Probe1Identity = 'probe1',
    [string]$Probe2Identity = 'probe2',
    [string]$Endpoint = '',
    [int]$DelayMsHostToProbes = 2000,
    [int]$DelayMsBetweenProbes = 1500
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg) {
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    Write-Host "[$ts] $msg" -ForegroundColor Cyan
}

function Write-Ok($msg) {
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    Write-Host "[$ts] $msg" -ForegroundColor Green
}

function Write-Warn($msg) {
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    Write-Host "[$ts] $msg" -ForegroundColor Yellow
}

function Write-Err($msg) {
    $ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    Write-Host "[$ts] $msg" -ForegroundColor Red
}

$solutionDir = $PSScriptRoot
$grpcPort = $Port + 1
if ([string]::IsNullOrWhiteSpace($Endpoint)) {
    $Endpoint = "localhost:$grpcPort"
}

Write-Info "Solution directory: $solutionDir"
Write-Info "HTTP2 host port: $Port | gRPC port: $grpcPort"
Write-Info "Endpoint for probes: $Endpoint"

# Helper: wait for gRPC port to accept connections
function Wait-PortOpen {
    param(
        [string]$TargetHost = 'localhost',
        [int]$Port,
        [int]$TimeoutMs = 15000,
        [int]$IntervalMs = 250
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try {
            $client = [System.Net.Sockets.TcpClient]::new()
            $async = $client.BeginConnect($TargetHost, $Port, $null, $null)
            if ($async.AsyncWaitHandle.WaitOne($IntervalMs)) {
                $client.EndConnect($async)
                $client.Close()
                return $true
            }
            $client.Close()
        } catch {
            # ignore and retry
        }
    }
    return $false
}

# Start host in background
Write-Info "Starting host (identity='$HostIdentity')..."
$hostJob = Start-Job -Name PercolatorHost -ScriptBlock {
    param($wd, $port, $identity)
    Set-Location $wd
    & dotnet run --project "source\Percolator.Node" -- host --port $port --identity $identity
} -ArgumentList $solutionDir, $Port, $HostIdentity

Write-Info "Waiting for gRPC port $grpcPort to open..."
if (-not (Wait-PortOpen -TargetHost 'localhost' -Port $grpcPort -TimeoutMs ([math]::Max($DelayMsHostToProbes,15000)))) {
    Write-Err "gRPC port $grpcPort did not open in time. Aborting."
    try {
        if ($hostJob -and $hostJob.State -eq 'Running') { Stop-Job -Job $hostJob -ErrorAction SilentlyContinue }
        Receive-Job -Job $hostJob -Keep -ErrorAction SilentlyContinue | Out-Null
    } finally {
        if ($hostJob) { Remove-Job -Job $hostJob -ErrorAction SilentlyContinue }
    }
    exit 1
}

# First probe
Write-Info "Running first dht-probe (self='$Probe1Identity' -> target='$HostIdentity')..."
try {
    Push-Location $solutionDir
    & dotnet run --project "source\Percolator.Node" -- dht-probe $Endpoint --target-identity $HostIdentity --self $Probe1Identity
    Write-Ok "First dht-probe completed."
}
catch {
    Write-Err "First dht-probe failed: $($_.Exception.Message)"
}
finally {
    Pop-Location
}

Write-Info "Waiting $DelayMsBetweenProbes ms before second probe..."
Start-Sleep -Milliseconds $DelayMsBetweenProbes

# Second probe
Write-Info "Running second dht-probe (self='$Probe2Identity' -> target='$HostIdentity')..."
try {
    Push-Location $solutionDir
    & dotnet run --project "source\Percolator.Node" -- dht-probe $Endpoint --target-identity $HostIdentity --self $Probe2Identity
    Write-Ok "Second dht-probe completed."
}
catch {
    Write-Err "Second dht-probe failed: $($_.Exception.Message)"
}
finally {
    Pop-Location
}

# Cleanup host
Write-Info "Stopping host..."
try {
    if ($hostJob -and $hostJob.State -eq 'Running') {
        Stop-Job -Job $hostJob -ErrorAction SilentlyContinue
    }
    Receive-Job -Job $hostJob -Keep -ErrorAction SilentlyContinue | Out-Null
}
finally {
    if ($hostJob) { Remove-Job -Job $hostJob -ErrorAction SilentlyContinue }
}

Write-Ok "All done."
