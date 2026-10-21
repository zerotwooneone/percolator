using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

public interface IPrivateRendezvousService
{
    BlindedLocator DeriveLocator(
        PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch epoch);

    IReadOnlyList<BlindedLocator> DeriveLocatorWindow(
        PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch currentEpoch);

    ValueTask<DomainResult> PublishEncryptedRendezvousAsync(
        BlindedLocator locator,
        EncryptedRendezvousRoute encryptedRoute,
        CancellationToken ct = default);

    ValueTask<DomainResult> PublishRendezvousAsync(
        BlindedLocator locator,
        string relayRouteDescriptor,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<BlindedRendezvousMatch>> ResolveRendezvousAsync(
        IReadOnlyList<BlindedLocator> locators,
        CancellationToken ct = default);
}
