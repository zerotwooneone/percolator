using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Percolator.Network;

namespace Percolator.Application.Network;

public class InMemoryPeerTrustStore : IPeerTrustManager
{
    private readonly ITrustedPeerStore _trustedPeerStore;
    private readonly ILogger<InMemoryPeerTrustStore> _logger;
    private readonly ConcurrentDictionary<PublicKeyHash, byte> _trustedHashes = new();

    public InMemoryPeerTrustStore(
        ITrustedPeerStore trustedPeerStore, 
        ILogger<InMemoryPeerTrustStore> logger)
    {
        _trustedPeerStore = trustedPeerStore;
        _logger = logger;
    }

    public void Initialize()
    {
        _logger.LogInformation("Initializing in-memory peer trust store...");
        var allTrustedHashes = _trustedPeerStore.GetAllAsync().GetAwaiter().GetResult();
        foreach (var hash in allTrustedHashes)
        {
            _trustedHashes.TryAdd(hash, 0);
            _logger.LogDebug("Loaded trusted peer with public key hash {Hash}", Convert.ToHexString(hash.Value));
        }
        _logger.LogInformation("In-memory peer trust store initialized with {Count} entries.", _trustedHashes.Count);
    }

    public bool IsTrusted(X509Certificate2 presentedCertificate)
    {
        AsymmetricAlgorithm? presentedKey = presentedCertificate.GetRSAPublicKey();
        if (presentedKey is null)
        {
            presentedKey = presentedCertificate.GetECDsaPublicKey();
        }

        if (presentedKey is null)
            return false;

        var spki = presentedKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);

        var publicKeyHash = new PublicKeyHash(hash);

        return _trustedHashes.ContainsKey(publicKeyHash);
    }

    public async Task AddTrustedPeer(X509Certificate2 certificate)
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
        var publicKeyHash = new PublicKeyHash(hash);

        if (_trustedHashes.TryAdd(publicKeyHash, 0))
        {
            await _trustedPeerStore.AddAsync(publicKeyHash);
            _logger.LogInformation("Added new trusted peer with public key hash {Hash}", Convert.ToHexString(hash));
        }
    }
}
