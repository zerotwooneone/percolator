# Test script for Percolator gRPC connection
# This script tests connections between two Percolator instances

param (
    [int]$ServerPort = 5000,  # Default server port
    [switch]$SkipBuild = $false,  # Skip build step
    [int]$NodeStartupWaitTime = 5,
    [switch]$DisableDiagnosticLogging = $false  # By default, diagnostic logging is enabled
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

    # Build arguments list with optional crypto logging
    $argList = @("host", "-p", "$port", "-i", "$identity")
    
    # Add diagnostic crypto logging flag if enabled
    if (-not $DisableDiagnosticLogging) {
        Write-Log "Enabling cryptographic diagnostic logging for $identity node"
        $argList += "--enable-crypto-logging"
    } else {
        Write-Log "Cryptographic diagnostic logging is disabled for $identity node"
    }
    
    $process = Start-Process -FilePath $processPath -ArgumentList $argList -PassThru -WindowStyle Normal
    
    Write-Log "$identity node started with PID $($process.Id)"
    return $process
}

# Send a message from one identity to another
function Send-Message {
    param(
        [string]$fromIdentity,
        [string]$toIdentity,
        [string]$toHost,
        [int]$toGrpcPort,
        [string]$message = "Test message"
    )
    
    Write-Log "=== Sending message from $fromIdentity to $toIdentity at $toHost`:$toGrpcPort ==="
    
    $clientPath = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe"
    $endpoint = "$toHost`:$toGrpcPort"
    
    # Build the send command with required parameters
    $sendArgs = @(
        "send", 
        $message,
        "--endpoint", $endpoint, 
        "--peer-name", $toIdentity, 
        "-i", $fromIdentity
    )
    
    # Add diagnostic crypto logging flag if enabled
    if (-not $DisableDiagnosticLogging) {
        Write-Log "Enabling cryptographic diagnostic logging for message sending"
        $sendArgs += "--enable-crypto-logging"
    } else {
        Write-Log "Cryptographic diagnostic logging is disabled for message sending"
    }
    
    Write-Log "Running: $clientPath $($sendArgs -join ' ')"
    $output = & $clientPath $sendArgs 2>&1
    
    # Log the output
    foreach ($line in $output) {
        Write-Log "OUTPUT: $line"
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
    
    # Start server node (alice)
    Write-Log "Starting Alice node on web port $aliceWebPort (gRPC: $aliceGrpcPort)..."
    $aliceNode = Start-PercolatorNode -identity "alice" -port $aliceWebPort -enableTlsDebug
    
    # Wait for server to initialize
    Wait-ForNodeStartup -seconds $NodeStartupWaitTime
    
    # Send message from Bob to Alice
    Send-Message -fromIdentity "bob" -toIdentity "alice" -toHost "localhost" -toGrpcPort $aliceGrpcPort -message "Hello from Bob!"
    
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
    
    Write-Log "Test complete. Results saved to $logFile"
}
