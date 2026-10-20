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
        Bitfield bitfield)
    {
        PeerId = peerId;
        Bitfield = bitfield;
        PeerChokingUs = PeerChokeStatus.Choked;
        WeChokingPeer = PeerChokeStatus.Choked;
        PeerInterestedInUs = PeerInterestStatus.NotInterested;
        WeInterestedInPeer = PeerInterestStatus.NotInterested;
    }

    public void UpdateBitfield(Bitfield bitfield) => Bitfield = bitfield;
    public void RecordHave(ChunkIndex chunkIndex) => Bitfield = Bitfield.WithChunk(chunkIndex);
    
    public void SetPeerChokingUs(PeerChokeStatus status) => PeerChokingUs = status;
    public void SetWeChokingPeer(PeerChokeStatus status) => WeChokingPeer = status;
    public void SetPeerInterestedInUs(PeerInterestStatus status) => PeerInterestedInUs = status;
    public void SetWeInterestedInPeer(PeerInterestStatus status) => WeInterestedInPeer = status;
}
