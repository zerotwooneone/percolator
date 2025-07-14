# Test script for Percolator gRPC connection
# This script tests connections between two Percolator instances

param (
    [int]$ServerPort = 5000,  # Default server port
    [switch]$SkipBuild = $false,  # Skip build step
    [int]$NodeStartupWaitTime = 5
)

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logFile = "grpc-test-$timestamp.log"
Write-Host "Starting gRPC connection test. Output will be saved to $logFile"

# Helper function for logging
function Write-Log {
    param([string]$message)
    
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $formattedMessage = "[$timestamp] $message"
    
    Write-Host $formattedMessage
    Add-Content -Path $logFile -Value $formattedMessage
}

# Start a Percolator node with given identity and port
function Start-PercolatorNode {
    param (
        [string]$identity,
        [int]$port,
        [switch]$enableTlsDebug
    )
    
    # Calculate gRPC port as port + 1 (matches Program.cs logic)
    $grpcPort = $port + 1
    
    Write-Log "Starting $identity node on web port $port (gRPC port: $grpcPort)..."
    $processPath = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe"
    
    if ($enableTlsDebug) {
        $env:DOTNET_SYSTEM_NET_SECURITY_TLS_DEBUG = "1"
    }
    
    $process = Start-Process -FilePath $processPath -ArgumentList "host", "-p", "$port", "-i", "$identity" -PassThru -WindowStyle Normal
    
    Write-Log "$identity node started with PID $($process.Id)"
    return $process
}

# Test a connection between two nodes
function Test-Connection {
    param(
        [string]$fromIdentity,
        [string]$toIdentity,
        [string]$toHost,
        [int]$toGrpcPort  # This is explicitly the gRPC port now
    )
    
    Write-Log "=== Testing connection from $fromIdentity to $toIdentity at $toHost`:$toGrpcPort ==="
    
    $clientPath = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe"
    $endpoint = "$toHost`:$toGrpcPort"
    $args = @("connect", "-e", $endpoint, "-i", "$fromIdentity")
    
    Write-Log "Running: $clientPath $args"
    $output = & $clientPath $args 2>&1
    
    # Log the output
    foreach ($line in $output) {
        Write-Log "OUTPUT: $line"
    }
    
    # Send a test message
    Write-Log "Sending a test message from $fromIdentity to $toIdentity..."
    $testMessage = "Test message from $fromIdentity"
    $sendArgs = @("send", $testMessage, "--endpoint", $endpoint, "--peer-name", $toIdentity, "-i", $fromIdentity)
    
    Write-Log "Running: $clientPath $sendArgs"
    $sendOutput = & $clientPath $sendArgs 2>&1
    
    foreach ($line in $sendOutput) {
        Write-Log "SEND OUTPUT: $line"
    }
}

# Stop a Percolator node
function Stop-PercolatorNode {
    param($process)
    
    if ($null -ne $process -and !$process.HasExited) {
        Write-Log "Stopping node with PID $($process.Id)..."
        Stop-Process -Id $process.Id -Force
        Write-Log "Node stopped"
    }
}

# Wait for a node to fully start
function Wait-ForNodeStartup {
    param([int]$seconds)
    
    Write-Log "Waiting $seconds seconds for nodes to initialize..."
    Start-Sleep -Seconds $seconds
}

# Main test flow
try {
    Write-Log "=== Starting Percolator gRPC Connection Test ==="
    
    # Build the solution first (unless skipped)
    if (!$SkipBuild) {
        Write-Log "Building solution..."
        dotnet build .\source\Percolator.sln
        
        if ($LASTEXITCODE -ne 0) {
            Write-Log "Build failed with exit code $LASTEXITCODE"
            exit $LASTEXITCODE
        }
        
        Write-Log "Build completed successfully."
    }
    
    # Calculate ports - server node uses ServerPort, client node uses ServerPort+10
    $aliceWebPort = $ServerPort
    $aliceGrpcPort = $aliceWebPort + 1  # gRPC port is web port + 1
    
    $bobWebPort = $ServerPort + 10
    $bobGrpcPort = $bobWebPort + 1      # gRPC port is web port + 1
    
    # Start server node (alice)
    Write-Log "Starting Alice node on web port $aliceWebPort (gRPC: $aliceGrpcPort)..."
    $aliceNode = Start-PercolatorNode -identity "alice" -port $aliceWebPort -enableTlsDebug
    
    # Wait for server to initialize
    Wait-ForNodeStartup -seconds $NodeStartupWaitTime
    
    # Start client node (bob)
    Write-Log "Starting Bob node on web port $bobWebPort (gRPC: $bobGrpcPort)..."
    $bobNode = Start-PercolatorNode -identity "bob" -port $bobWebPort -enableTlsDebug
    
    # Wait for client to initialize
    Wait-ForNodeStartup -seconds $NodeStartupWaitTime
    
    # Test connection from bob to alice - Note: Using the gRPC port
    Test-Connection -fromIdentity "bob" -toIdentity "alice" -toHost "localhost" -toGrpcPort $aliceGrpcPort
    
    # User prompt to end test
    Write-Log "Test complete. Press any key to stop the test and cleanup..."
    $null = $host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
} 
catch {
    Write-Log "An error occurred: $_"
}
finally {
    # Clean up
    if ($null -ne $aliceNode) { Stop-PercolatorNode $aliceNode }
    if ($null -ne $bobNode) { Stop-PercolatorNode $bobNode }
    
    Write-Log "Test complete. Results saved to $logFile"
}
