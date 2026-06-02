using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

var listenPort = configuration.GetValue("ListenPort", 5001);
var targetPort = configuration.GetValue("TargetPort", 5002);
var cleanupCertFiles = configuration.GetValue("CleanupCertFiles", true);

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CONFIG] ListenPort: {listenPort}, TargetPort: {targetPort}, CleanupCertFiles: {cleanupCertFiles}");

// ============================================================================
// IDENTITY & EPHEMERAL CERTIFICATE GENERATION
// ============================================================================

// Create dedicated temp directory for cert files
var certDir = Path.Combine(Path.GetTempPath(), "TofuTlsGrpcTest");
if (!Directory.Exists(certDir))
{
    Directory.CreateDirectory(certDir);
}

// Track cert files for cleanup
var certFiles = new List<string>();

// 1. Generate a dummy Identity Signing Key (ECDsa)
using var identitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var identitySpki = identitySigningKey.ExportSubjectPublicKeyInfo();
var identityHash = SHA256.HashData(identitySpki);
var identityHashString = Convert.ToHexString(identityHash);

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [IDENTITY] Generated Identity Signing Key.");
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [IDENTITY] Identity SPKI Hash: {identityHashString}");

X509Certificate2 GenerateEphemeralCertificate(ECDsa identityKey)
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Generating new ephemeral self-signed TLS certificate using identity key...");

    // Use the identity key as the TLS certificate's public key
    var distinguishedName = new X500DistinguishedName($"CN=localhost");
    var request = new CertificateRequest(distinguishedName, identityKey, HashAlgorithmName.SHA256);

    // Minimal TLS 1.3 extensions
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

    // SAN for localhost
    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName("localhost");
    sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
    sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
    request.CertificateExtensions.Add(sanBuilder.Build());

    using var ephemeralCert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(365));

    // Write to disk - Windows SChannel requires persistent key storage for TLS 1.3
    var tempPfxPath = Path.Combine(certDir, $"percolator_tls_cert_{Guid.NewGuid()}.pfx");
    var pfxBytes = ephemeralCert.Export(X509ContentType.Pfx, "password");
    File.WriteAllBytes(tempPfxPath, pfxBytes);
    var loadedCert = X509CertificateLoader.LoadPkcs12FromFile(tempPfxPath, "password", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

    certFiles.Add(tempPfxPath);
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Certificate written to disk: {tempPfxPath}");
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Certificate loaded. Subject: {loadedCert.Subject}, HasPrivateKey: {loadedCert.HasPrivateKey}");
    return loadedCert;
}

// ============================================================================
// SERVER STARTUP (Delayed On-Demand Host Pattern)
// ============================================================================

// Simulate "App Startup" without the server
var serverManager = new GrpcServerManager();
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] App running. Waiting 2 seconds before 'User Logs In'...");
await Task.Delay(2000);

// Simulate "Identity Selected -> BootstrapAsync()"
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] User logged in. Generating Identity & Certificate...");
var dynamicServerCertificate = GenerateEphemeralCertificate(identitySigningKey);
var dynamicServerCertHash = dynamicServerCertificate.GetCertHashString(HashAlgorithmName.SHA256);
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Ephemeral Certificate ready. TLS Cert Hash: {dynamicServerCertHash}");

// Start the server on-demand
await serverManager.StartAsync(listenPort, dynamicServerCertificate);

// ============================================================================
// RANDOM DELAY TO PREVENT RACING
// ============================================================================

var random = new Random();
var delayMs = random.Next(2000, 5000);
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] Waiting {delayMs}ms before starting client...");
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
            // Enable TLS 1.3 for privacy (certificate payload encryption)
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
            // Blind Trust: X3DH handles actual security. Accept any TLS certificate.
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }
    };

    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Creating gRPC channel to https://localhost:{targetPort}...");
    var channel = GrpcChannel.ForAddress($"https://localhost:{targetPort}", new GrpcChannelOptions
    {
        HttpHandler = httpHandler
    });

    var client = new PingPongService.PingPongServiceClient(channel);

    for (int i = 1; i <= 3; i++)
    {
        try
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Sending ping #{i}...");
            var response = await client.SendPingAsync(new PingRequest { Counter = i, SenderName = $"Client on {listenPort}" });
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Pong received: {response.Message} (Counter: {response.Counter})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Exception on ping #{i}: {ex}");
        }
        await Task.Delay(1000);
    }
    await channel.ShutdownAsync();
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Channel shut down successfully.");
}
catch (Exception ex)
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] FATAL: {ex}");
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] FATAL Stack: {ex.StackTrace}");
}

// ============================================================================
// SHUTDOWN
// ============================================================================

// Wait 5 seconds before stopping server in case the other client started late
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] Waiting 5 seconds before shutdown...");
await Task.Delay(5000);

await serverManager.StopAsync();

// Dispose certificate to release file lock before cleanup
dynamicServerCertificate.Dispose();

// Cleanup cert files if requested
if (cleanupCertFiles)
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLEANUP] Deleting {certFiles.Count} certificate file(s)...");
    foreach (var certFile in certFiles)
    {
        try
        {
            if (File.Exists(certFile))
            {
                File.Delete(certFile);
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLEANUP] Deleted: {certFile}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLEANUP] Failed to delete {certFile}: {ex.Message}");
        }
    }
    
    // Try to remove the directory if it's empty
    try
    {
        if (Directory.Exists(certDir) && !Directory.EnumerateFileSystemEntries(certDir).Any())
        {
            Directory.Delete(certDir);
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLEANUP] Removed empty directory: {certDir}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLEANUP] Failed to remove directory {certDir}: {ex.Message}");
    }
}
else
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLEANUP] Skipping cleanup (CleanupCertFiles=false). Cert files remain in: {certDir}");
}

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] Application shut down successfully.");


public class PingPongServiceImpl(int listenPort) : PingPongService.PingPongServiceBase
{
    public override Task<PongResponse> SendPing(PingRequest request, ServerCallContext context)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Ping received from '{request.SenderName}', sending Pong");
        return Task.FromResult(new PongResponse { Counter = request.Counter, Message = $"Pong from server on {listenPort}" });
    }
}

// NOTE: Simulate the shared library constant for testing
public static class Oids
{
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
    // PeerIdentityKey OID no longer needed - Identity Key is the TLS public key
}

public class GrpcServerManager
{
    private WebApplication? _app;

    public async Task StartAsync(int port, X509Certificate2 cert)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MANAGER] Dynamically building gRPC WebApplication for port {port}...");
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MANAGER] Configuring Kestrel to listen on localhost port {port}...");
            options.ListenLocalhost(port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
                // Use our custom ECDSA certificate with TLS 1.3
                listenOptions.UseHttps(cert, httpsOptions =>
                {
                    httpsOptions.SslProtocols = System.Security.Authentication.SslProtocols.Tls13;
                });
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Kestrel configured for HTTP/2 + TLS 1.3 (ECDSA CERT) on localhost:{port}");
            });

            // Log connection events
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton<PingPongServiceImpl>(_ => new PingPongServiceImpl(port));

        _app = builder.Build();
        _app.MapGrpcService<PingPongServiceImpl>();

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MANAGER] Starting gRPC server on port {port}...");
        try
        {
            await _app.StartAsync();
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MANAGER] gRPC Server started successfully. Listening on https://localhost:{port}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MANAGER] FATAL: Failed to start gRPC server: {ex.Message}");
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MANAGER] FATAL Stack: {ex.StackTrace}");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MANAGER] Stopping gRPC Server...");
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
