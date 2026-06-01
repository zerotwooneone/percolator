using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TofuTlsGrpcTest;

// ============================================================================
// CONFIGURATION SETUP
// ============================================================================

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CONFIG] Building configuration...");

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddCommandLine(args)
    .Build();

var listenPort = configuration.GetValue<int>("ListenPort", 5001);
var targetPort = configuration.GetValue<int>("TargetPort", 5002);

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CONFIG] ListenPort: {listenPort}, TargetPort: {targetPort}");

// ============================================================================
// CERTIFICATE GENERATION (Persisted to File)
// ============================================================================

var certFilePath = $"server_cert_{listenPort}.pfx";
const string certPassword = "testpassword";

X509Certificate2 LoadOrCreateCertificate()
{
    // If certificate file exists, load it
    if (File.Exists(certFilePath))
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Loading existing certificate from {certFilePath}...");
#pragma warning disable SYSLIB0057
        var loadedCertificate = new X509Certificate2(certFilePath, certPassword);
#pragma warning restore SYSLIB0057
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Certificate loaded from file.");
        return loadedCertificate;
    }
    
    // Generate new self-signed certificate
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Generating new self-signed certificate for localhost...");
    
    var distinguishedName = new X500DistinguishedName($"CN=localhost");
    
    using var ecdsa = ECDsa.Create();
    var request = new CertificateRequest(distinguishedName, ecdsa, HashAlgorithmName.SHA256);
    
    // Add extensions for a proper certificate
    request.CertificateExtensions.Add(
        new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
            critical: true));
    
    request.CertificateExtensions.Add(
        new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
            critical: false));
    
    // Add Subject Alternative Name for localhost
    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName("localhost");
    sanBuilder.AddDnsName("127.0.0.1");
    request.CertificateExtensions.Add(sanBuilder.Build());
    
    var ephemeralCert = request.CreateSelfSigned(
        DateTimeOffset.Now.AddDays(-1),
        DateTimeOffset.Now.AddDays(365));
    
    // Export to PFX with password and save to file
    var exportBytes = ephemeralCert.Export(X509ContentType.Pfx, certPassword);
    File.WriteAllBytes(certFilePath, exportBytes);
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Certificate saved to {certFilePath}");
    
    // Re-import from file to ensure proper Windows Schannel binding
#pragma warning disable SYSLIB0057
    var certificate = new X509Certificate2(certFilePath, certPassword);
#pragma warning restore SYSLIB0057
    
    return certificate;
}

var serverCertificate = LoadOrCreateCertificate();
var serverCertHash = serverCertificate.GetCertHashString(HashAlgorithmName.SHA256);

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Certificate ready. SHA-256 Hash: {serverCertHash}");

// ============================================================================
// TOFU CERTIFICATE VALIDATION
// ============================================================================

bool ValidateTofuCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
{
    if (certificate == null)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] CRITICAL: No certificate received from remote endpoint!");
        return false;
    }

    var certHash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Validation triggered. Received Cert Hash: {certHash}");
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] SSL Policy Errors: {sslPolicyErrors}");

    var trustFile = $"trusted_peer_{targetPort}.txt";
    
    // First Use: File doesn't exist
    if (!File.Exists(trustFile))
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] ==============================================================================");
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] WARNING: FIRST USE - TRUSTING NEW CERTIFICATE");
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Trust File: {trustFile}");
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Cert Hash: {certHash}");
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] ==============================================================================");
        
        try
        {
            File.WriteAllText(trustFile, certHash);
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Trust file created successfully. Connection ALLOWED.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] ERROR: Failed to create trust file: {ex.Message}");
            return false;
        }
    }
    
    // Subsequent Use: File exists, verify hash matches
    try
    {
        var storedHash = File.ReadAllText(trustFile).Trim();
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Trust file found. Stored Hash: {storedHash}");
        
        if (storedHash.Equals(certHash, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Hashes match! Connection ALLOWED.");
            return true;
        }
        else
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] ==============================================================================");
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] CRITICAL SECURITY FAILURE: CERTIFICATE HASH MISMATCH!");
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Expected: {storedHash}");
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Received: {certHash}");
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] ==============================================================================");
            return false;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] ERROR: Failed to read trust file: {ex.Message}");
        return false;
    }
}

// ============================================================================
// SERVER STARTUP
// ============================================================================

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Starting gRPC server on port {listenPort}...");

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(listenPort, listenOptions =>
    {
        try
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            listenOptions.UseHttps(serverCertificate);
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Kestrel configured for HTTP/2 + HTTPS on port {listenPort}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] ERROR configuring Kestrel HTTPS: {ex.Message}");
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Stack Trace: {ex.StackTrace}");
            throw;
        }
    });
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<PingPongServiceImpl>(sp => new PingPongServiceImpl(listenPort));

var app = builder.Build();

app.MapGrpcService<PingPongServiceImpl>();

// Start server in background
var serverTask = Task.Run(async () =>
{
    try
    {
        await app.RunAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] FATAL: Server startup failed: {ex.Message}");
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Stack Trace: {ex.StackTrace}");
    }
});

// Wait a moment for server to start
await Task.Delay(1000);

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Started on port {listenPort}. My Cert Hash: {serverCertHash}");

// ============================================================================
// RANDOM DELAY TO PREVENT RACING
// ============================================================================

var random = new Random();
var delayMs = random.Next(2000, 10001);
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] Waiting {delayMs}ms before starting client (to prevent racing)...");
await Task.Delay(delayMs);

// ============================================================================
// CLIENT STARTUP WITH TOFU
// ============================================================================

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Starting gRPC client to target port {targetPort}...");

try
{
    var httpHandler = new SocketsHttpHandler
    {
        SslOptions =
        {
            RemoteCertificateValidationCallback = ValidateTofuCertificate
        }
    };
    
    var channel = GrpcChannel.ForAddress($"https://localhost:{targetPort}", new GrpcChannelOptions
    {
        HttpHandler = httpHandler,
        UnsafeUseInsecureChannelCallCredentials = true
    });
    
    var client = new PingPongService.PingPongServiceClient(channel);
    
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] gRPC channel created. Starting ping loop...");
    
    // Send 5 pings, 1 second apart
    for (int i = 1; i <= 5; i++)
    {
        try
        {
            var request = new PingRequest
            {
                Counter = i,
                SenderName = $"Client on port {listenPort}"
            };
            
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Ping sent (Counter: {i})");
            
            var response = await client.SendPingAsync(request);
            
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Pong received: {response.Message} (Counter: {response.Counter})");
        }
        catch (RpcException ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] RPC Exception: {ex.Status}");
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Stack Trace: {ex.StackTrace}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Exception: {ex.Message}");
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Stack Trace: {ex.StackTrace}");
        }
        
        if (i < 5)
        {
            await Task.Delay(1000);
        }
    }
    
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Ping loop completed.");
    
    await channel.ShutdownAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] FATAL: Client setup failed: {ex.Message}");
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Stack Trace: {ex.StackTrace}");
}

// ============================================================================
// SHUTDOWN
// ============================================================================

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] Application shutting down. Press Ctrl+C to exit immediately.");
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] Server will continue running in background.");

// Keep server running
await Task.Delay(Timeout.Infinite);

// ============================================================================
// gRPC SERVICE IMPLEMENTATION
// ============================================================================

public class PingPongServiceImpl : PingPongService.PingPongServiceBase
{
    private readonly int _listenPort;
    
    public PingPongServiceImpl(int listenPort)
    {
        _listenPort = listenPort;
    }
    
    public override Task<PongResponse> SendPing(PingRequest request, ServerCallContext context)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Ping received from '{request.SenderName}' (Counter: {request.Counter}), sending Pong (Counter: {request.Counter})");
        
        var response = new PongResponse
        {
            Counter = request.Counter,
            Message = $"Pong from server on port {_listenPort}"
        };
        
        return Task.FromResult(response);
    }
}
