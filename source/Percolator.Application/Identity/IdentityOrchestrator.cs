using Microsoft.Extensions.Logging;
using Percolator.Application.Configuration;
using Percolator.Identity;
using Microsoft.Extensions.Options;
using Percolator.Identity.Model;
using System.Security.Cryptography;

namespace Percolator.Application.Identity;

public class IdentityOrchestrator : IIdentityOrchestrator
{
    private readonly ISelfIdentityKeysStore _keysStore;
    private readonly ISelfIdentityRepositoryOld _selfIdentityRepository;
    private readonly ILogger<IdentityOrchestrator> _logger;
    private readonly NodeOptions _options;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public IdentityOrchestrator(
        ISelfIdentityKeysStore keysStore,
        ISelfIdentityRepositoryOld selfIdentityRepository,
        ILogger<IdentityOrchestrator> logger,
        IOptions<NodeOptions> options,
        ActiveIdentityContext activeIdentityContext)
    {
        _keysStore = keysStore;
        _selfIdentityRepository = selfIdentityRepository;
        _logger = logger;
        _options = options.Value;
        _activeIdentityContext = activeIdentityContext;
    }

    public async Task ResolveIdentityAsync(string identityName, CancellationToken cancellationToken, string? fallbackIdentityName=null)
    {
        if (string.IsNullOrEmpty(identityName))
        {
            throw new ArgumentException("Identity name cannot be null or empty.", nameof(identityName));
        }

        // Resolve and assign SelfIdentityId for scoping
        var dto = await _selfIdentityRepository.GetByNameWithFallbackAsync(identityName, fallbackIdentityName??String.Empty).ConfigureAwait(false);
        if (dto is null)
        {
            throw new InvalidOperationException($"Identity {identityName} not found");
        }
        var selfId = dto.Id;
        var keys = await _keysStore.LoadAsync(selfId, cancellationToken).ConfigureAwait(false);
        if (keys is null)
        {
            // Generate new X3DH keys and persist
            var ikSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            keys = new X3dhKeys(ikSigning, spk);
            await _keysStore.SaveAsync(selfId, keys, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Generated and saved new X3DH keys for identity {IdentityName} (SelfIdentityId={SelfIdentityId})", identityName, selfId);
        }
        var identity = new IdentityRecord(dto.PeerId, identityName, dto.Name)  with { SelfIdentityId = selfId };

        _activeIdentityContext.Identity = identity;
        _activeIdentityContext.Keys = keys;

        var publicKeyBytes = keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        _logger.LogInformation("Successfully loaded identity {IdentityName} with public key {PublicKey} Hash {PublicKeyHash}", 
            identityName, 
            Convert.ToBase64String(publicKeyBytes),
            Convert.ToBase64String(SHA256.HashData(publicKeyBytes)));
    }
}
