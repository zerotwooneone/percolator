param(
    [int]$Port = 5000,
    [int]$Retries = 10,
    [int]$RetryDelayMs = 500,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = 'Stop'

# Ensure Development environment so Program.cs auto-applies EF Core migrations
$env:ASPNETCORE_ENVIRONMENT = 'Development'

# Ensure relative project paths resolve even if called from another CWD
$prev = Get-Location
Set-Location -Path $PSScriptRoot

function Invoke-Percolator {
    param(
        [Parameter(Mandatory=$true)][string[]]$CmdArgs,
        [switch]$Wait,
        [switch]$NoNewWindow
    )

    # Prefer dotnet run to ensure latest build
    $cmd = "dotnet"
    $fullArgs = @("run", "--project", "Percolator.Node", "-c", $Configuration, "--") + $CmdArgs

    if ($Wait) {
        Write-Host ("exec: dotnet {0}" -f ($fullArgs -join ' ')) -ForegroundColor DarkGray
        & $cmd @fullArgs
        return $LASTEXITCODE
    } else {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $cmd
        $psi.Arguments = ($fullArgs -join ' ')
        $psi.RedirectStandardOutput = $false
        $psi.RedirectStandardError = $false
        $psi.UseShellExecute = $true
        $psi.CreateNoWindow = -not $NoNewWindow
        $p = [System.Diagnostics.Process]::Start($psi)
        return $p
    }
}

Write-Host "Ensuring identities (idempotent)..." -ForegroundColor Cyan
Invoke-Percolator -CmdArgs @("create-identity", "--name", "host") -Wait | Out-Null
Invoke-Percolator -CmdArgs @("create-identity", "--name", "alice") -Wait | Out-Null
Invoke-Percolator -CmdArgs @("create-identity", "--name", "bob") -Wait | Out-Null

Write-Host "Starting host on port $Port..." -ForegroundColor Cyan
$hostProc = Invoke-Percolator -CmdArgs @("host", "--selfIdentity", "host", "--port", "$Port")
Start-Sleep -Seconds 2

function Invoke-With-Retry {
    param(
        [Parameter(Mandatory=$true)][string[]]$Args,
        [int]$Retries = 10,
        [int]$DelayMs = 500
    )

    for ($i=0; $i -lt $Retries; $i++) {
        Write-Host ("Attempt {0}/{1}: percolator {2}" -f ($i+1), $Retries, ($Args -join ' ')) -ForegroundColor DarkGray
        $dotnetArgs = @("run", "--project", "Percolator.Node", "-c", $Configuration, "--") + $Args
        Write-Host ("exec: dotnet {0}" -f ($dotnetArgs -join ' ')) -ForegroundColor DarkGray
        & dotnet @dotnetArgs
        $ec = $LASTEXITCODE
        if ($ec -eq 0) { return 0 }
        Start-Sleep -Milliseconds $DelayMs
    }
    return 1
}

try {
    $endpoint = "localhost:$Port"

    Write-Host "Alice probing Host (expect empty nearest list)..." -ForegroundColor Yellow
    $ec = Invoke-With-Retry -Args @("dht-probe", $endpoint, "--target-identity", "host", "--selfIdentity", "alice") -Retries $Retries -DelayMs $RetryDelayMs
    if ($ec -ne 0) { throw "Alice probe failed after $Retries attempts" }

    Write-Host "Bob probing Host (expect nearest to include Alice)..." -ForegroundColor Yellow
    $ec = Invoke-With-Retry -Args @("dht-probe", $endpoint, "--target-identity", "host", "--selfIdentity", "bob") -Retries $Retries -DelayMs $RetryDelayMs
    if ($ec -ne 0) { throw "Bob probe failed after $Retries attempts" }
}
finally {
    if ($hostProc -and -not $hostProc.HasExited) {
        Write-Host "Stopping host..." -ForegroundColor Cyan
        try { $hostProc.Kill() } catch {}
        try { $hostProc.WaitForExit(3000) } catch {}
    }

# restore previous CWD
Set-Location -Path $prev
}
