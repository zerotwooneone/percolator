using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Percolator.Network;
using Percolator.Infrastructure.Network.Tls;

namespace Percolator.Infrastructure.Network.Trust;

public class InMemoryPeerTrustStore : IPeerTrustManager
{
    private readonly ITrustedPeerStore _trustedPeerStore;
    private readonly ILogger<InMemoryPeerTrustStore> _logger;
    private readonly ConcurrentDictionary<PublicKeyHash, byte> _trustedHashes = new();
    private readonly SharedCertificateManager _sharedCertificateManager;
    private string? _sharedCertificateThumbprint;

    public InMemoryPeerTrustStore(
        ITrustedPeerStore trustedPeerStore, 
        ILogger<InMemoryPeerTrustStore> logger,
        SharedCertificateManager sharedCertificateManager)
    {
        _trustedPeerStore = trustedPeerStore;
        _logger = logger;
        _sharedCertificateManager = sharedCertificateManager;
    }

    public void Initialize()
    {
        _logger.LogInformation("Initializing in-memory peer trust store with shared certificate support...");
        
        // First, load the shared certificate and register it as trusted
        try
        {
            var sharedCertificate = _sharedCertificateManager.GetServerCertificate();
            _sharedCertificateThumbprint = sharedCertificate.Thumbprint;
            
            // Add the shared certificate to our trusted store
            AddTrustedPeerInternal(sharedCertificate).GetAwaiter().GetResult();
            _logger.LogInformation("Registered shared certificate with thumbprint {Thumbprint} as trusted", _sharedCertificateThumbprint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register shared certificate as trusted");
        }
        
        // Then load any other trusted certificates from the store
        var allTrustedHashes = _trustedPeerStore.GetAllAsync().GetAwaiter().GetResult();
        foreach (var hash in allTrustedHashes)
        {
            _trustedHashes.TryAdd(hash, 0);
            _logger.LogDebug("Loaded trusted peer with public key hash {Hash}", Convert.ToHexString(hash.ToArray()));
        }
        _logger.LogInformation("In-memory peer trust store initialized with {Count} entries.", _trustedHashes.Count);
    }

    public bool IsTrusted(X509Certificate2 presentedCertificate)
    {
        // First check if this is our shared certificate by comparing thumbprints
        if (!string.IsNullOrEmpty(_sharedCertificateThumbprint) && 
            presentedCertificate.Thumbprint.Equals(_sharedCertificateThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Certificate with thumbprint {Thumbprint} matched shared certificate - trusted", presentedCertificate.Thumbprint);
            return true;
        }
        
        // Fall back to the public key hash check for backward compatibility
        AsymmetricAlgorithm? presentedKey = presentedCertificate.GetRSAPublicKey();
        if (presentedKey is null)
        {
            presentedKey = presentedCertificate.GetECDsaPublicKey();
        }

        if (presentedKey is null)
            return false;

        var spki = presentedKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);

        var publicKeyHash = PublicKeyHash.FromBytes(hash);

        return _trustedHashes.ContainsKey(publicKeyHash);
    }

    public async Task AddTrustedPeer(X509Certificate2 certificate)
    {
        await AddTrustedPeerInternal(certificate).ConfigureAwait(false);
    }
    
    private async Task AddTrustedPeerInternal(X509Certificate2 certificate)
    {
        AsymmetricAlgorithm? presentedKey = certificate.GetRSAPublicKey();
        if (presentedKey is null)
        { 
            presentedKey = certificate.GetECDsaPublicKey();
        }

        if (presentedKey is null)
        {
            _logger.LogWarning("Attempted to add a trusted peer with a certificate that has an unsupported or null public key.");
            return;
        }

        var spki = presentedKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        var publicKeyHash = PublicKeyHash.FromBytes(hash);

        if (_trustedHashes.TryAdd(publicKeyHash, 0))
        {
            await _trustedPeerStore.AddAsync(publicKeyHash).ConfigureAwait(false);
            _logger.LogInformation("Added new trusted peer with public key hash {Hash}", Convert.ToHexString(hash));
        }
    }
}
