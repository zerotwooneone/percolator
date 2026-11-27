using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class FakePeerInvitationService : IPeerInvitationService
{
    public PeerInvitation? ResultToReturn { get; set; }
    public DomainError? ErrorToReturn { get; set; }

    public Task<DomainResult<PeerInvitation>> CreatePeerInvitationAsync(
        PublicIdentityId ownerId,
        RelayEndpoint relayEndpoint,
        CancellationToken ct = default)
    {
        if (ErrorToReturn.HasValue)
        {
            return Task.FromResult(DomainResult<PeerInvitation>.Failure(ErrorToReturn.Value));
        }


        var invitation = ResultToReturn ?? PeerInvitation.Create(
            ownerId,
            IdentityKey.FromBytes(new byte[32]),
            relayEndpoint,
            BlindedRoutingToken.New()).Value;

        return Task.FromResult(DomainResult<PeerInvitation>.Success(invitation));
    }
}

public sealed class FakePeerConnectionService : IPeerConnectionService
{
    public PeerConnectionResult? ResultToReturn { get; set; }
    public DomainError? ErrorToReturn { get; set; }
    public PeerInvitation? LastReceivedInvitation { get; private set; }

    public Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
        PublicIdentityId targetPeerId,
        DeviceId? targetDeviceId = null,
        RelayEndpoint? relayEndpoint = null,
        CancellationToken ct = default)
    {
        if (ErrorToReturn.HasValue)
        {
            return Task.FromResult(DomainResult<PeerConnectionResult>.Failure(ErrorToReturn.Value));
        }

        var result = ResultToReturn ?? new PeerConnectionResult(
            Percolator.Domain.Channels.ValueObjects.ChannelId.New(),
            DeviceId.Primary);

        return Task.FromResult(DomainResult<PeerConnectionResult>.Success(result));
    }

    public Task<DomainResult<PeerConnectionResult>> ConnectWithInvitationAsync(
        PublicIdentityId ownerId,
        PeerInvitation invitation,
        CancellationToken ct = default)
    {
        LastReceivedInvitation = invitation;

        if (ErrorToReturn.HasValue)
        {
            return Task.FromResult(DomainResult<PeerConnectionResult>.Failure(ErrorToReturn.Value));
        }


        var result = ResultToReturn ?? new PeerConnectionResult(
            Percolator.Domain.Channels.ValueObjects.ChannelId.New(),
            DeviceId.Primary);

        return Task.FromResult(DomainResult<PeerConnectionResult>.Success(result));
    }
}
