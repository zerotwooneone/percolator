param (
    [int]$AlicePort = 5001,
    [int]$BobPort = 5002
)

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logFile = "send-test-$timestamp.log"
Write-Host "Starting TOFU send test. Output will be saved to $logFile"

function Write-Log {
    param([string]$message)
    
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $formattedMessage = "[$timestamp] $message"
    
    Write-Host $formattedMessage
    Add-Content -Path $logFile -Value $formattedMessage
}

function Start-PercolatorNode {
    param (
        [string]$identity,
        [int]$port
    )
    
    Write-Log "Starting $identity node on port $port..."
    $processPath = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe"
    $process = Start-Process -FilePath $processPath -ArgumentList "host", "-p", "$port", "-i", "$identity" -PassThru -WindowStyle Minimized
    
    Write-Log "$identity node started with PID $($process.Id)"
    return $process
}

function Stop-PercolatorNode {
    param($process)
    
    if ($process -ne $null -and !$process.HasExited) {
        Write-Log "Stopping node with PID $($process.Id)..."
        Stop-Process -Id $process.Id -Force
        Write-Log "Node stopped"
    }
}

function Wait-ForNodeStartup {
    param([int]$seconds)
    
    Write-Log "Waiting $seconds seconds for nodes to initialize..."
    Start-Sleep -Seconds $seconds
}

function Get-AllCommands {
    $nodePath = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe"
    Write-Log "=== Available Commands ==="
    $helpOutput = & $nodePath --help 2>&1
    $helpOutput | ForEach-Object { Write-Log $_ }
    Write-Log ""
    
    # Get help for the send command
    Write-Log "=== Send Command Help ==="
    $sendHelp = & $nodePath send --help 2>&1
    $sendHelp | ForEach-Object { Write-Log $_ }
    Write-Log ""
}

# Main test script
try {
    Write-Log "=== Percolator TOFU Send Test ==="
    Write-Log "Date: $(Get-Date)"
    Write-Log ""

    Write-Log "=== Environment Information ==="
    Write-Log "OS: $([System.Environment]::OSVersion.VersionString)"
    Write-Log ".NET: $([System.Runtime.InteropServices.RuntimeEnvironment]::GetSystemVersion())"
    Write-Log ""

    # Build the project
    Write-Log "=== Building Percolator ==="
    & dotnet build source\Percolator.Node\Percolator.Node.csproj
    if ($LASTEXITCODE -ne 0) {
        Write-Log "Build failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Log "Build successful"
    Write-Log ""
    
    # Get available commands
    Get-AllCommands
    
    # Start Alice and Bob nodes
    $aliceProcess = Start-PercolatorNode -identity "alice" -port $AlicePort
    $bobProcess = Start-PercolatorNode -identity "bob" -port $BobPort

    # Wait for nodes to initialize
    Wait-ForNodeStartup -seconds 10

    # Send message from Alice to Bob using correct argument format
    Write-Log "=== Test: Alice sending message to Bob ==="
    $message = "Hello from Alice!"
    $sendCommand = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe send `"$message`" -i alice --endpoint localhost:$BobPort --peer-name bob"
    Write-Log "Executing: $sendCommand"
    $sendOutput = & cmd /c $sendCommand 2>&1
    $sendOutput | ForEach-Object { Write-Log $_ }

    # Wait a bit for the message to be processed
    Start-Sleep -Seconds 5

    # Try the connect command first
    Write-Log "=== Test: Alice connecting to Bob ==="
    $connectCommand = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe connect localhost:$BobPort -i alice --peer-name bob"
    Write-Log "Executing: $connectCommand"
    $connectOutput = & cmd /c $connectCommand 2>&1
    $connectOutput | ForEach-Object { Write-Log $_ }
    
    # Wait for connection
    Start-Sleep -Seconds 5
    
    # List available commands for message viewing
    Write-Log "=== Test: Checking available commands ==="
    $listCommands = ".\source\Percolator.Node\bin\Debug\net9.0-windows\Percolator.Node.exe --help"
    $listCommandsOutput = & cmd /c $listCommands 2>&1
    $listCommandsOutput | ForEach-Object { Write-Log $_ }

    Write-Log "=== TOFU Send Test Complete ==="
}
catch {
    Write-Log "Error: $_"
}
finally {
    # Clean up processes
    Stop-PercolatorNode -process $aliceProcess
    Stop-PercolatorNode -process $bobProcess
    Write-Log "Test complete. Results saved to $logFile"
    Write-Host "Test complete. Results saved to $logFile"
}
