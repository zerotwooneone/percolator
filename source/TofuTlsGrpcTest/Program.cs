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
// IDENTITY & EPHEMERAL CERTIFICATE GENERATION (In-Memory Only)
// ============================================================================

// 1. Generate a dummy Identity Signing Key (ECDsa)
using var identitySigningKey = ECDsa.Create();
var identitySpki = identitySigningKey.ExportSubjectPublicKeyInfo();
var identityHash = SHA256.HashData(identitySpki);
var identityHashString = Convert.ToHexString(identityHash);

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [IDENTITY] Generated Identity Signing Key.");
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [IDENTITY] Identity SPKI Hash: {identityHashString}");

X509Certificate2 GenerateEphemeralCertificate()
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Generating new ephemeral self-signed TLS certificate...");
    
    // Simulating Percolator.Cryptography.CertificateGenerator
    var distinguishedName = new X500DistinguishedName($"CN=localhost");
    var request = new CertificateRequest(distinguishedName, identitySigningKey, HashAlgorithmName.SHA256);
    
    // Basic Extensions
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, critical: true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(Oids.ServerAuthentication) }, critical: false));
    
    // SAN
    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName("localhost");
    sanBuilder.AddDnsName("127.0.0.1");
    request.CertificateExtensions.Add(sanBuilder.Build());

    // Custom OID for Identity Key
    // Write ASN.1 DER Octet String containing the SPKI
    var asnWriter = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
    asnWriter.WriteOctetString(identitySpki);
    var encodedPublicKey = asnWriter.Encode();
    request.CertificateExtensions.Add(new X509Extension(Oids.PeerIdentityKey, encodedPublicKey, false));
    
    var ephemeralCert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(365));
    
    // Fix Windows Schannel binding issue via pure in-memory PFX roundtrip (no disk I/O)
    var exportBytes = ephemeralCert.Export(X509ContentType.Pfx, "password");
#pragma warning disable SYSLIB0057
    var memoryCertificate = new X509Certificate2(exportBytes, "password", X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    
    return memoryCertificate;
}

var serverCertificate = GenerateEphemeralCertificate();
var serverCertHash = serverCertificate.GetCertHashString(HashAlgorithmName.SHA256);

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CERT] Ephemeral Certificate ready. TLS Cert Hash: {serverCertHash}");

// ============================================================================
// TOFU CERTIFICATE VALIDATION (Targeting Identity Key)
// ============================================================================

bool ValidateTofuCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
{
    if (certificate == null)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] CRITICAL: No certificate received!");
        return false;
    }

    var cert2 = new X509Certificate2(certificate);
    
    // 1. Extract Identity Key from Custom OID
    var identityExtension = cert2.Extensions[Oids.PeerIdentityKey];
    if (identityExtension == null)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] CRITICAL: Certificate missing PeerIdentityKey OID!");
        return false;
    }

    // Decode ASN.1 Octet String back to raw SPKI
    var asnReader = new System.Formats.Asn1.AsnReader(identityExtension.RawData, System.Formats.Asn1.AsnEncodingRules.DER);
    var extractedSpki = asnReader.ReadOctetString();
    
    // 2. Mathematically Verify Binding
    // Ensure the TLS certificate was actually signed by the extracted SPKI
    var expectedSpkiFromCert = cert2.PublicKey.ExportSubjectPublicKeyInfo();
    if (!extractedSpki.SequenceEqual(expectedSpkiFromCert))
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] CRITICAL: Embedded SPKI does not match TLS Certificate Public Key!");
        return false;
    }
    
    // 3. Perform TOFU on Identity Key Hash
    var extractedIdentityHash = Convert.ToHexString(SHA256.HashData(extractedSpki));
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Extraction Successful. Target Identity Hash: {extractedIdentityHash}");

    var trustFile = $"trusted_identity_{targetPort}.txt"; // Simulating PeerId repository lookup
    
    if (!File.Exists(trustFile))
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] WARNING: FIRST USE - TRUSTING NEW IDENTITY KEY");
        File.WriteAllText(trustFile, extractedIdentityHash);
        return true;
    }
    
    var storedHash = File.ReadAllText(trustFile).Trim();
    if (storedHash.Equals(extractedIdentityHash, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] Identity Hash Matches! Connection ALLOWED.");
        return true;
    }
    else
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] [TOFU] CRITICAL: IDENTITY KEY HASH MISMATCH!");
        return false;
    }
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
var dynamicServerCertificate = GenerateEphemeralCertificate();
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
            RemoteCertificateValidationCallback = ValidateTofuCertificate
        }
    };
    
    var channel = GrpcChannel.ForAddress($"https://localhost:{targetPort}", new GrpcChannelOptions
    {
        HttpHandler = httpHandler
    });
    
    var client = new PingPongService.PingPongServiceClient(channel);
    
    for (int i = 1; i <= 3; i++)
    {
        try
        {
            var response = await client.SendPingAsync(new PingRequest { Counter = i, SenderName = $"Client on {listenPort}" });
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Pong received: {response.Message} (Counter: {response.Counter})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] Exception: {ex.Message}");
        }
        await Task.Delay(1000);
    }
    await channel.ShutdownAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [CLIENT] FATAL: {ex.Message}");
}

// ============================================================================
// SHUTDOWN
// ============================================================================

await serverManager.StopAsync();
Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INIT] Application shut down successfully.");


public class PingPongServiceImpl : PingPongService.PingPongServiceBase
{
    private readonly int _listenPort;
    public PingPongServiceImpl(int listenPort) => _listenPort = listenPort;
    
    public override Task<PongResponse> SendPing(PingRequest request, ServerCallContext context)
    {
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Ping received from '{request.SenderName}', sending Pong");
        return Task.FromResult(new PongResponse { Counter = request.Counter, Message = $"Pong from server on {_listenPort}" });
    }
}

// NOTE: Simulate the shared library constant for testing
public static class Oids
{
    public const string PeerIdentityKey = "1.3.6.1.4.1.58824.1.1";
    public const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
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
            options.ListenAnyIP(port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
                listenOptions.UseHttps(cert);
                Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SERVER] Kestrel configured for HTTP/2 + HTTPS on port {port}");
            });
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton<PingPongServiceImpl>(sp => new PingPongServiceImpl(port));

        _app = builder.Build();
        _app.MapGrpcService<PingPongServiceImpl>();

        await _app.StartAsync();
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [MANAGER] gRPC Server started successfully in the background.");
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
