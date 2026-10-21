using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public enum PeerFilterDecision : byte
{
    Allowed = 0x01,
    Blocked = 0x02
}

public enum RelayFilterDecision : byte
{
    Allowed = 0x01,
    Blocked = 0x02
}

public sealed record FileTransferFilterPolicy(
    PublicIdentityId OwnerId,
    IReadOnlySet<PublicIdentityId> BlockedPeers,
    IReadOnlySet<RelayPipeEndpointUri> BlockedRelays)
{
    public static FileTransferFilterPolicy DefaultPermissive(PublicIdentityId ownerId) =>
        new(ownerId, new HashSet<PublicIdentityId>(), new HashSet<RelayPipeEndpointUri>());

    public static FileTransferFilterPolicy CreateDefault() =>
        DefaultPermissive(PublicIdentityId.New());

    public FileTransferFilterPolicy BlockPeer(PublicIdentityId peerId) =>
        this with { BlockedPeers = new HashSet<PublicIdentityId>(BlockedPeers) { peerId } };

    public FileTransferFilterPolicy BlockRelay(RelayPipeEndpointUri relayUri) =>
        this with { BlockedRelays = new HashSet<RelayPipeEndpointUri>(BlockedRelays) { relayUri } };

    public PeerFilterDecision EvaluatePeer(PublicIdentityId peerId) =>
        BlockedPeers.Contains(peerId) ? PeerFilterDecision.Blocked : PeerFilterDecision.Allowed;

    public RelayFilterDecision EvaluateRelay(RelayPipeEndpointUri relayUri) =>
        BlockedRelays.Contains(relayUri) ? RelayFilterDecision.Blocked : RelayFilterDecision.Allowed;
}
