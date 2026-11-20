param(
    [int]$Port = 5000,
    [int]$Retries = 2,
    [int]$RetryDelayMs = 500,
    [string]$Configuration = "Debug",
    [string]$LogDir = "$PSScriptRoot\logs"
)

$ErrorActionPreference = 'Stop'

# Ensure Development environment so Program.cs auto-applies EF Core migrations
$env:ASPNETCORE_ENVIRONMENT = 'Development'

# Ensure we run relative to the repo source root
$prev = Get-Location
Set-Location -Path $PSScriptRoot

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

# Build once to avoid dotnet run argument-forwarding quirks
Write-Host "Building Percolator.Node ($Configuration)..." -ForegroundColor DarkCyan
& dotnet build .\Percolator.Node\Percolator.Node.csproj -c $Configuration | Out-Null
$dllCandidates = Get-ChildItem -Path (Join-Path $PSScriptRoot "Percolator.Node\bin\$Configuration") -Recurse -Filter Percolator.Node.dll | Sort-Object LastWriteTime -Descending
if (-not $dllCandidates -or $dllCandidates.Count -eq 0) { throw "Build output not found under Percolator.Node\\bin\\$Configuration" }
$nodeDll = $dllCandidates[0].FullName
Write-Host "Using Node DLL: $nodeDll" -ForegroundColor DarkGray

function Start-Logged {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string[]]$CmdArgs
    )
    $logPath = Join-Path $LogDir ("{0}_{1}.log" -f $Name, $timestamp)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "dotnet"
    # Execute the built DLL directly; do NOT include a bare "--" (that's only for dotnet run)
    $argList = @($nodeDll) + $CmdArgs
    $psi.Arguments = ($argList -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    # Ensure Development environment flows into the spawned host process
    $psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = 'Development'
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $null = $proc.Start()
    $stdTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    Start-Job -ScriptBlock {
        param($p, $std, $err, $lp)
        $p.WaitForExit()
        $out = $std.Result
        $e = $err.Result
        Set-Content -Path $lp -Value $out
        if ($e) { Add-Content -Path $lp -Value ("`n[stderr]`n" + $e) }
    } -ArgumentList $proc, $stdTask, $errTask, $logPath | Out-Null
    return @{ Process = $proc; Log = $logPath }
}

function Invoke-PercolatorSync {
    param([string[]]$CmdArgs)
    $dotnetArgs = @($nodeDll) + $CmdArgs
    Write-Host ("exec: dotnet {0}" -f ($dotnetArgs -join ' ')) -ForegroundColor DarkGray
    & dotnet @dotnetArgs
    return $LASTEXITCODE
}

function Invoke-With-Retry {
    param(
        [string[]]$CmdArgs,
        [int]$Retries,
        [int]$DelayMs,
        [string]$Name,
        [string]$LogDir
    )
    for ($i=0; $i -lt $Retries; $i++) {
        $attempt = $i + 1
        Write-Host ("Attempt {0}/{1}: percolator {2}" -f $attempt, $Retries, ($CmdArgs -join ' ')) -ForegroundColor DarkGray

        # Build arguments and log path for this attempt
        $dotnetArgs = @($nodeDll) + $CmdArgs
        $attemptLog = Join-Path $LogDir ("{0}_{1}_attempt{2}.log" -f $Name, $timestamp, $attempt)
        Write-Host ("exec: dotnet {0}" -f ($dotnetArgs -join ' ')) -ForegroundColor DarkGray

        # Execute and tee stdout+stderr to the attempt log
        & dotnet @dotnetArgs *>&1 | Tee-Object -FilePath $attemptLog | Out-Null
        $ec = $LASTEXITCODE
        if ($ec -eq 0) {
            Write-Host ("Success. Output captured: {0}" -f $attemptLog) -ForegroundColor DarkGreen
            return 0
        }
        else {
            Write-Host ("Failed with exit code {0}. Output captured: {1}" -f $ec, $attemptLog) -ForegroundColor DarkYellow
        }
        Start-Sleep -Milliseconds $DelayMs
    }
    return 1
}

Write-Host "Ensuring identities (idempotent)..." -ForegroundColor Cyan
Invoke-PercolatorSync -CmdArgs @("create-identity", "--name", "host") | Out-Null
Invoke-PercolatorSync -CmdArgs @("create-identity", "--name", "alice", "--dbFile", "alice.db") | Out-Null
Invoke-PercolatorSync -CmdArgs @("create-identity", "--name", "bob", "--dbFile", "bob.db") | Out-Null

Write-Host "Starting host with logging..." -ForegroundColor Cyan
$hostRun = Start-Logged -Name "host" -CmdArgs @("host", "--selfIdentity", "host", "--port", "$Port")
$hostProc = $hostRun.Process
$hostLog  = $hostRun.Log
Write-Host "Host log: $hostLog" -ForegroundColor DarkCyan
# Allow a bit more time for the host to bind HTTP/2 and apply migrations
Start-Sleep -Seconds 4

try {
    # The host listens HTTP on $Port and gRPC on ($Port + 1). Probe needs the gRPC endpoint.
    $grpcPort = $Port + 1
    $endpoint = "localhost:$grpcPort"

    Write-Host "Alice probing Host (expect empty nearest list)..." -ForegroundColor Yellow
    $ec = Invoke-With-Retry -CmdArgs @("dht-probe", $endpoint, "--target-identity", "host", "--selfIdentity", "alice", "--dbFile", "alice.db") -Retries $Retries -DelayMs $RetryDelayMs -Name "alice_dht_probe" -LogDir $LogDir
    if ($ec -ne 0) { throw "Alice probe failed after $Retries attempts" }

    Write-Host "Bob probing Host (expect nearest to include Alice)..." -ForegroundColor Yellow
    $ec = Invoke-With-Retry -CmdArgs @("dht-probe", $endpoint, "--target-identity", "host", "--selfIdentity", "bob", "--dbFile", "bob.db") -Retries $Retries -DelayMs $RetryDelayMs -Name "bob_dht_probe" -LogDir $LogDir
    if ($ec -ne 0) { throw "Bob probe failed after $Retries attempts" }
}
finally {
    if ($hostProc -and -not $hostProc.HasExited) {
        Write-Host "Stopping host..." -ForegroundColor Cyan
        try { $hostProc.Kill() } catch {}
        try { $hostProc.WaitForExit(3000) } catch {}
    }
    # Give the background logging job a moment to flush output
    try { Get-Job | Wait-Job -Any -Timeout 3 | Out-Null } catch {}
    # small retry loop for log presence
    for ($i=0; $i -lt 3 -and -not (Test-Path $hostLog); $i++) { Start-Sleep -Milliseconds 300 }
    if (Test-Path $hostLog) {
        Write-Host "--- Host log tail ---" -ForegroundColor DarkCyan
        Get-Content $hostLog -Tail 50
    }
    # restore previous CWD
    Set-Location -Path $prev
}
