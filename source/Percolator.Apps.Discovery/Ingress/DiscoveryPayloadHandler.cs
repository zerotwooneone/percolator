using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Ingress;

/// <summary>
/// Ingress payload dispatcher for Discovery plugin (AppId = 0x02).
/// Enforces session authentication: a peer cannot submit registration payloads on behalf of another identity.
/// </summary>
public sealed class DiscoveryPayloadHandler : IAppPayloadHandler
{
    public AppId TargetAppId => AppId.Discovery;

    private readonly IRelayPeerDirectory _directory;
    private readonly IPrivateRendezvousService _rendezvousService;

    public DiscoveryPayloadHandler(
        IRelayPeerDirectory directory,
        IPrivateRendezvousService rendezvousService)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _rendezvousService = rendezvousService ?? throw new ArgumentNullException(nameof(rendezvousService));
    }

    public async ValueTask<DomainResult> HandleInboundAsync(
        InboundPayloadContext context,
        CancellationToken ct = default)
    {
        if (context.AppId != TargetAppId)
        {
            return DomainResult.Failure(new DomainError(
                "APP_ID_MISMATCH", $"Payload has AppId '{context.AppId}' but handler expects '{TargetAppId}'."));
        }

        if (context.Payload.IsEmpty)
        {
            return DomainResult.Failure(new DomainError(
                "EMPTY_PAYLOAD", "Inbound discovery payload cannot be empty."));
        }

        return DomainResult.Success();
    }

    /// <summary>
    /// Processes a registration payload while strictly verifying that the sender is the authenticated session owner.
    /// Anti-impersonation invariant.
    /// </summary>
    public async ValueTask<DomainResult> HandleRegistrationAsync(
        InboundPayloadContext context,
        DiscoveryRegistrationPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Anti-impersonation: the registration payload's target identity must match the session sender
        if (payload.PeerId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError(
                "UNAUTHORIZED_REGISTRATION",
                $"Peer '{context.SenderIdentityId}' cannot update discovery visibility for identity '{payload.PeerId}'."));
        }

        var updateResult = await _directory.UpdateVisibilityAsync(payload.PeerId, payload.Visibility, ct).ConfigureAwait(false);
        return updateResult;
    }

    /// <summary>
    /// Processes a query payload on a relay/hosting node and returns candidate descriptors.
    /// </summary>
    public async ValueTask<DomainResult<NearbyDiscoveryResponsePayload>> HandleNearbyQueryAsync(
        InboundPayloadContext context,
        NearbyDiscoveryQueryPayload query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_directory.Policy == RelayDiscoveryPolicy.Disabled)
        {
            return DomainResult<NearbyDiscoveryResponsePayload>.Failure(new DomainError(
                "RELAY_DISCOVERY_DISABLED", "Discovery services are disabled on this relay."));
        }

        var limit = DiscoveryResultLimit.Clamp(query.RequestedLimit);
        var discoverable = await _directory.GetDiscoverablePeersAsync(ct).ConfigureAwait(false);

        var descriptors = new List<PeerDiscoveryDescriptor>();
        foreach (var reg in discoverable)
        {
            // Do not return the requester themselves in the candidate list
            if (reg.PeerId == context.SenderIdentityId)
            {
                continue;
            }

            descriptors.Add(new PeerDiscoveryDescriptor(
                reg.PeerId,
                DeliveryRoute.RelayedOneToOne));

            if (descriptors.Count >= limit.Value)
            {
                break;
            }
        }

        return DomainResult<NearbyDiscoveryResponsePayload>.Success(new NearbyDiscoveryResponsePayload(descriptors));
    }
}
