using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.SwarmCoordination;

public sealed class SwarmMember : IEntity<PublicIdentityId>
{
    public PublicIdentityId Id => PeerId;
    public PublicIdentityId PeerId { get; }
    public Bitfield Bitfield { get; private set; }
    
    public PeerChokeStatus PeerChokingUs { get; private set; }
    public PeerChokeStatus WeChokingPeer { get; private set; }
    public PeerInterestStatus PeerInterestedInUs { get; private set; }
    public PeerInterestStatus WeInterestedInPeer { get; private set; }

    public SwarmPeerRole Role => Bitfield.IsComplete ? SwarmPeerRole.Seeder : SwarmPeerRole.Leecher;

    public SwarmMember(
        PublicIdentityId peerId,
        Bitfield bitfield,
        PeerChokeStatus peerChokingUs = PeerChokeStatus.Choked,
        PeerChokeStatus weChokingPeer = PeerChokeStatus.Choked,
        PeerInterestStatus peerInterestedInUs = PeerInterestStatus.NotInterested,
        PeerInterestStatus weInterestedInPeer = PeerInterestStatus.NotInterested)
    {
        ArgumentNullException.ThrowIfNull(bitfield);
        PeerId = peerId;
        Bitfield = bitfield;
        PeerChokingUs = peerChokingUs;
        WeChokingPeer = weChokingPeer;
        PeerInterestedInUs = peerInterestedInUs;
        WeInterestedInPeer = weInterestedInPeer;
    }

    internal void UpdateBitfield(Bitfield bitfield) => Bitfield = bitfield;
    internal void RecordHave(ChunkIndex chunkIndex) => Bitfield = Bitfield.WithChunk(chunkIndex);
    
    internal void SetPeerChokingUs(PeerChokeStatus status) => PeerChokingUs = status;
    internal void SetWeChokingPeer(PeerChokeStatus status) => WeChokingPeer = status;
    internal void SetPeerInterestedInUs(PeerInterestStatus status) => PeerInterestedInUs = status;
    internal void SetWeInterestedInPeer(PeerInterestStatus status) => WeInterestedInPeer = status;
}
