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
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ILogger<IdentityOrchestrator> _logger;
    private readonly NodeOptions _options;
    private readonly ActiveIdentityContext _activeIdentityContext;

    public IdentityOrchestrator(
        ISelfIdentityKeysStore keysStore,
        ISelfIdentityRepository selfIdentityRepository,
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

    public async Task ResolveIdentityAsync(SelfId selfId, CancellationToken cancellationToken)
    {
        // Resolve and assign SelfIdentityId for scoping
        var dto = await _selfIdentityRepository.GetByIdAsync(selfId,cancellationToken).ConfigureAwait(false);
        if (dto is null)
        {
            throw new InvalidOperationException($"Identity with id {selfId} not found");
        }
        var keys = await _keysStore.LoadAsync(selfId, cancellationToken).ConfigureAwait(false);
        if (keys is null)
        {
            // Generate new X3DH keys and persist
            var ikSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            keys = new X3dhKeys(ikSigning, spk);
            await _keysStore.SaveAsync(selfId, keys, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Generated and saved new X3DH keys for identity {IdentityName} (SelfIdentityId={SelfIdentityId})", dto.DisplayName, selfId);
        }
        //todo: figure out what to use for participant id in chat conversations
        var peerId = Guid.NewGuid();
        
        //todo: figure out what name to use
        var identityName = dto.DisplayName?.Value ?? dto.Id.ToString();
        var identity = new IdentityRecord(peerId, identityName, null)  with { SelfIdentityId = selfId };

        _activeIdentityContext.Identity = identity;
        _activeIdentityContext.Keys = keys;

        var publicKeyBytes = keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        _logger.LogInformation("Successfully loaded identity {IdentityName} with public key {PublicKey} Hash {PublicKeyHash}", 
            identityName, 
            Convert.ToBase64String(publicKeyBytes),
            Convert.ToBase64String(SHA256.HashData(publicKeyBytes)));
    }
}
