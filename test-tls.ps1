# Test TLS Handshake between nodes
# This script tests TLS connectivity by starting servers and running TLS diagnostics

$outputPath = "tls-diagnostics-$(Get-Date -Format 'yyyyMMdd-HHmmss').log"
Write-Host "Starting TLS diagnostics. Output will be saved to $outputPath" -ForegroundColor Cyan

function Write-Log {
    param([string]$message)
    Write-Host $message
    Add-Content -Path $outputPath -Value $message
}

Write-Log "=== Percolator TLS Diagnostics ==="
Write-Log "Date: $(Get-Date)"
Write-Log ""

# Environment information
Write-Log "=== Environment Information ==="
$osInfo = (Get-CimInstance Win32_OperatingSystem).Caption
Write-Log "OS: $osInfo"
$netVersion = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
Write-Log ".NET: $netVersion"
Write-Log ""

# Step 1: Make sure Percolator is built
Write-Log "=== Building Percolator ==="
try {
    dotnet build .\source\Percolator.Node\Percolator.Node.csproj | Tee-Object -FilePath $outputPath -Append
    if ($LASTEXITCODE -ne 0) {
        Write-Log "ERROR: Build failed with exit code $LASTEXITCODE"
        exit 1
    }
    Write-Log "Build successful"
} catch {
    Write-Log "ERROR: Exception during build: $_"
    exit 1
}
Write-Log ""

# Define paths and parameters
$exePath = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe"
$serverPort = 5001
$serverIdentity = "server1"

# Run the host command with --help to see options
Write-Log "=== Host Command Options ==="
& $exePath host --help | Tee-Object -FilePath $outputPath -Append
Write-Log ""

# Test 1: Start a server and test TLS connectivity
Write-Log "=== Test 1: TLS Debug Test ==="
Write-Log "Starting server on port $serverPort with identity $serverIdentity..."

# Start server in a separate window
$serverProcess = Start-Process -FilePath $exePath -ArgumentList "host", "-p", $serverPort, "-i", $serverIdentity -PassThru -WindowStyle Normal
Write-Log "Server started with PID $($serverProcess.Id)"
Start-Sleep -Seconds 10  # Give server more time to fully initialize

# Run TLS debug test
Write-Log "Running TLS debug test against localhost:$serverPort..."
& $exePath tls-debug localhost $serverPort | Tee-Object -FilePath $outputPath -Append
Write-Log ""

# Stop server
if (!$serverProcess.HasExited) {
    Write-Log "Stopping server..."
    Stop-Process -Id $serverProcess.Id -Force
    Write-Log "Server stopped"
}

Write-Log "=== Diagnostics Complete ==="
Write-Log "Results saved to $outputPath"
Write-Host "Diagnostics complete. Results saved to $outputPath" -ForegroundColor Green
