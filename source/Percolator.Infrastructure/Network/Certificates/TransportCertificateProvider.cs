using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Percolator.Identity;

namespace Percolator.Infrastructure.Network.Certificates;

public class TransportCertificateProvider : ITransportCertificateProvider
{
    private readonly ILogger<TransportCertificateProvider> _logger;
    private readonly TlsOptions _tlsOptions;
    private readonly string _certDirectory;

    public TransportCertificateProvider(
        ILogger<TransportCertificateProvider> logger,
        IOptions<TlsOptions> tlsOptions,
        IOptions<StorageOptions> storageOptions)
    {
        _logger = logger;
        _tlsOptions = tlsOptions.Value;
        
        // Use the configured storage path
        _certDirectory = storageOptions.Value.Path;
        
        if (!Directory.Exists(_certDirectory))
        {
            Directory.CreateDirectory(_certDirectory);
        }
    }

    public async Task<X509Certificate2> GetValidCertificateAsync(SelfId selfId, CancellationToken ct)
    {
        var certPath = Path.Combine(_certDirectory, $"tls_cert_{selfId}.pfx");

        // Check if file exists
        if (!File.Exists(certPath))
        {
            _logger.LogInformation("Certificate file not found, generating new certificate for identity {IdentityId}", selfId);
            return await GenerateAndSaveCertificateAsync(selfId, certPath, ct);
        }

        // Load existing certificate and check age
        try
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(
                certPath,
                _tlsOptions.Certificate.Password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

            // Check certificate age using NotBefore/NotAfter
            var age = DateTime.UtcNow - cert.NotBefore;
            if (age > TimeSpan.FromDays(_tlsOptions.Certificate.MaxAgeDays))
            {
                _logger.LogInformation("Certificate is older than {MaxAgeDays} days, rotating for identity {IdentityId}",
                    _tlsOptions.Certificate.MaxAgeDays, selfId);

                // Delete the persisted CNG key from the OS to prevent container leak
                using (var privateKey = cert.GetECDsaPrivateKey())
                {
                    if (privateKey is ECDsaCng cngKey)
                    {
                        cngKey.Key.Delete();
                    }
                }

                // Free the unmanaged .NET handle
                cert.Dispose();

                // Generate new certificate
                return await GenerateAndSaveCertificateAsync(selfId, certPath, ct);
            }

            return cert;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load certificate for identity {IdentityId}, generating new one", selfId);
            return await GenerateAndSaveCertificateAsync(selfId, certPath, ct);
        }
    }

    private async Task<X509Certificate2> GenerateAndSaveCertificateAsync(SelfId selfId, string certPath, CancellationToken ct)
    {
        // Generate a completely independent, random ECDSA (P-256) key pair
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // Create certificate request
        var request = new CertificateRequest(
            $"CN=Percolator-{selfId}",
            ecdsa,
            HashAlgorithmName.SHA256);

        // Add minimal TLS 1.3 extensions
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // ServerAuthentication
                critical: false));

        // SAN (localhost and loopback IPs)
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build(critical: false));

        // Create self-signed certificate valid for 1 year
        using var cert = request.CreateSelfSigned(
            notBefore: DateTimeOffset.UtcNow,
            notAfter: DateTimeOffset.UtcNow.AddYears(1));

        // Export to PFX with static password
        var pfxBytes = cert.Export(X509ContentType.Pkcs12, _tlsOptions.Certificate.Password);

        // Write to disk with retry-backoff policy for antivirus scanning
        await WriteCertificateWithRetryAsync(certPath, pfxBytes, ct);

        // Load with PersistKeySet for Windows SChannel compatibility
        return X509CertificateLoader.LoadPkcs12FromFile(
            certPath,
            _tlsOptions.Certificate.Password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    private async Task WriteCertificateWithRetryAsync(string path, byte[] pfxBytes, CancellationToken ct)
    {
        var maxRetries = _tlsOptions.RetryPolicy.MaxRetries;
        var delay = TimeSpan.FromMilliseconds(_tlsOptions.RetryPolicy.InitialDelayMs);

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await File.WriteAllBytesAsync(path, pfxBytes, ct);
                return;
            }
            catch (IOException ex) when (attempt < maxRetries - 1)
            {
                _logger.LogWarning(ex, "Failed to write certificate file (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms",
                    attempt + 1, maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * _tlsOptions.RetryPolicy.BackoffMultiplier);
            }
        }

        // Final attempt will throw if it fails
        await File.WriteAllBytesAsync(path, pfxBytes, ct);
    }
}
