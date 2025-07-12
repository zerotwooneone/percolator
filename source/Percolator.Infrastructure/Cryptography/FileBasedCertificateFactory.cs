using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Infrastructure.Cryptography;

public class FileBasedCertificateFactory : ICertificateFactory
{
    private readonly ICredentialService _credentialService;
    private readonly ILogger<FileBasedCertificateFactory> _logger;

    public FileBasedCertificateFactory(ICredentialService credentialService, ILogger<FileBasedCertificateFactory> logger)
    {
        _credentialService = credentialService;
        _logger = logger;
    }

    public X509Certificate2 GetOrCreatePeerCertificate(string identityName, AsymmetricAlgorithm keyPair, byte[] publicIdentitySigningKey)
    {
        var basePath = IdentityPathHelper.GetBasePath(identityName);
        var certPath = Path.Combine(basePath, "tls.pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);

        // Note: This is synchronous, which is not ideal for GetOrCreateCredentialAsync.
        // This part of the design may need to be revisited to be fully async.
        var password = _credentialService.GetOrCreateCredentialAsync(identityName, "tls-cert-password").GetAwaiter().GetResult();

        if (File.Exists(certPath))
        {
            _logger.LogInformation("Loading existing TLS certificate for {IdentityName} from {Path}", identityName, certPath);
            try
            {
                var pfxBytes = File.ReadAllBytes(certPath);
                var loadedCertificate = X509CertificateLoader.LoadPkcs12(pfxBytes, password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
                _logger.LogInformation("Loaded certificate. HasPrivateKey: {HasPrivateKey}", loadedCertificate.HasPrivateKey);
                return loadedCertificate;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load existing PFX certificate for {IdentityName}. A new one will be created.", identityName);
            }
        }

        _logger.LogInformation("Creating new TLS certificate for {IdentityName}", identityName);
        var newCertificate = CertificateGenerator.CreateTlsCertificate((ECDsa)keyPair, identityName, publicIdentitySigningKey);
        _logger.LogInformation("Created new certificate. HasPrivateKey: {HasPrivateKey}", newCertificate.HasPrivateKey);

        var newPfxBytes = newCertificate.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(certPath, newPfxBytes);
        _logger.LogInformation("Saved new TLS certificate for {IdentityName} to {Path}", identityName, certPath);

        return newCertificate;
    }
}
