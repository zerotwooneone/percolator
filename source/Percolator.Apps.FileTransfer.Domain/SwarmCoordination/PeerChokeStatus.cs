namespace Percolator.Apps.FileTransfer.Domain.SwarmCoordination;

public enum PeerChokeStatus : byte
{
    Choked = 0x01,
    Unchoked = 0x02
}
