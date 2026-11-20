namespace Percolator.Apps.FileTransfer.Domain.SwarmCoordination;

public enum SwarmPeerRole : byte
{
    Leecher = 0x01,
    Seeder = 0x02
}
