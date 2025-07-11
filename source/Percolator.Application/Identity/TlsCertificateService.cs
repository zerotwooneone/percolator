using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public class TlsCertificateService : ITlsCertificateService
{
    private readonly IIdentityService _identityService;
    private readonly IKeyManagementService _keyManagementService;
    private readonly ICredentialService _credentialService;
    private readonly ILogger<TlsCertificateService> _logger;

    public TlsCertificateService(
        IIdentityService identityService,
        IKeyManagementService keyManagementService,
        ICredentialService credentialService,
        ILogger<TlsCertificateService> logger)
    {
        _identityService = identityService;
        _keyManagementService = keyManagementService;
        _credentialService = credentialService;
        _logger = logger;
    }

    public async Task<X509Certificate2> GetOrCreateTlsCertificateAsync(string identityName, byte[] publicIdentitySigningKey)
    {
        var identityRecord = await _identityService.GetIdentityRecordAsync(identityName);
        if (identityRecord is null)
        {
            throw new System.InvalidOperationException($"Identity '{identityName}' not found.");
        }

        var basePath = Percolator.Identity.IdentityPathHelper.GetBasePath(identityName);
        var certPath = Path.Combine(basePath, "tls.pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);

        var password = await _credentialService.GetOrCreateCredentialAsync(identityName, "tls-cert-password");

        if (File.Exists(certPath))
        {
            _logger.LogInformation("Loading existing TLS certificate for {IdentityName} from {Path}", identityName, certPath);
            try
            {
                var pfxBytes = await File.ReadAllBytesAsync(certPath);
                var loadedCertificate = X509CertificateLoader.LoadPkcs12(pfxBytes, password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
                _logger.LogInformation("Loaded certificate. HasPrivateKey: {HasPrivateKey}", loadedCertificate.HasPrivateKey);
                return loadedCertificate;
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load existing PFX certificate for {IdentityName}. A new one will be created.", identityName);
            }
        }

        X3dhKeys keys;
        try
        {
            keys = await _keyManagementService.GetKeysAsync(identityName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogInformation("No existing keys found for {IdentityName}, creating new ones.", identityName);
            keys = await _keyManagementService.CreateKeysAsync(identityName);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            _logger.LogError(ex, "Failed to load or decrypt existing keys for {IdentityName}. A new set will be created.", identityName);
            keys = await _keyManagementService.CreateKeysAsync(identityName);
        }

        _logger.LogInformation("Creating new TLS certificate for {IdentityName}", identityName);
        var newCertificate = CertificateGenerator.CreateTlsCertificate(keys.IdentitySigningKey, identityName, publicIdentitySigningKey);
        _logger.LogInformation("Created new certificate. HasPrivateKey: {HasPrivateKey}", newCertificate.HasPrivateKey);

        var newPfxBytes = newCertificate.Export(X509ContentType.Pfx, password);
        await File.WriteAllBytesAsync(certPath, newPfxBytes);
        _logger.LogInformation("Saved new TLS certificate for {IdentityName} to {Path}", identityName, certPath);

        return newCertificate;
    }
}
