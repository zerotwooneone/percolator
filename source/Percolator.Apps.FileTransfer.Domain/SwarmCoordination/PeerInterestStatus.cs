namespace Percolator.Apps.FileTransfer.Domain.SwarmCoordination;

public enum PeerInterestStatus : byte
{
    NotInterested = 0x01,
    Interested = 0x02
}
