using Microsoft.Extensions.Logging;
using Percolator.Application.Configuration;
using Percolator.Identity;
using Microsoft.Extensions.Options;
using Percolator.Identity.Model;
using System.Security.Cryptography;
using MediatR;
using Percolator.Application.Messaging;
using Percolator.Identity.DomainEvents;

namespace Percolator.Application.Identity;

public class IdentityOrchestrator : IIdentityOrchestrator
{
    private readonly ISelfIdentityKeysStore _keysStore;
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly ILogger<IdentityOrchestrator> _logger;
    private readonly NodeOptions _options;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IPublisher _publisher;

    public IdentityOrchestrator(
        ISelfIdentityKeysStore keysStore,
        ISelfIdentityRepository selfIdentityRepository,
        ILogger<IdentityOrchestrator> logger,
        IOptions<NodeOptions> options,
        ActiveIdentityContext activeIdentityContext,
        IPublisher publisher)
    {
        _keysStore = keysStore;
        _selfIdentityRepository = selfIdentityRepository;
        _logger = logger;
        _options = options.Value;
        _activeIdentityContext = activeIdentityContext;
        _publisher = publisher;
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

            // Extract the public SPKI bytes and update the domain aggregate to trigger persistence sync
            var spki = ikSigning.PublicKey.ExportSubjectPublicKeyInfo();
            dto.AddKey(spki, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, DateTimeOffset.UtcNow);
            await _selfIdentityRepository.SaveAsync(dto, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Ensure existing keys are P-256, otherwise X3DH will fail when mixed with P-256 peers.
            var ikCurve = keys.IdentitySigningKey.ExportParameters(false).Curve.Oid.Value;
            var spkCurve = keys.SignedPreKey.ExportParameters(false).Curve.Oid.Value;
            var p256 = ECCurve.NamedCurves.nistP256.Oid.Value;
            if (!string.Equals(ikCurve, p256, StringComparison.Ordinal) || !string.Equals(spkCurve, p256, StringComparison.Ordinal))
            {
                try
                {
                    keys.Dispose();
                }
                catch
                {
                }

                var ikSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                keys = new X3dhKeys(ikSigning, spk);
                await _keysStore.SaveAsync(selfId, keys, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Regenerated X3DH keys for identity {IdentityName} because existing keys were not P-256 (SelfIdentityId={SelfIdentityId})", dto.DisplayName, selfId);
            }
        }
        var peerId = dto.PeerId.Value;
        var identityName = dto.DisplayName?.Value ?? dto.Id.ToString();
        var identity = new IdentityRecord(peerId, identityName, null) with { SelfIdentityId = selfId, PeerId = dto.PeerId, ListeningPort = dto.ListeningPort };

        _activeIdentityContext.SetActiveIdentity(identity, keys);

        var publicKeyBytes = keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        _logger.LogInformation("Successfully loaded identity {IdentityName} with public key {PublicKey} Hash {PublicKeyHash}", 
            identityName, 
            Convert.ToBase64String(publicKeyBytes),
            Convert.ToBase64String(SHA256.HashData(publicKeyBytes)));

        // Publish ActiveIdentityLoadedEvent to trigger downstream infrastructure (e.g. gRPC server startup)
        var domainEvent = new ActiveIdentityLoadedEvent(selfId);
        var notification = new DomainEventNotification<ActiveIdentityLoadedEvent>(domainEvent);
        await _publisher.Publish(notification, cancellationToken).ConfigureAwait(false);
    }
}
