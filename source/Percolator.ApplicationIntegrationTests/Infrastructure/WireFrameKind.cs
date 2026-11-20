namespace Percolator.Application2IntegrationTests.Infrastructure;

public enum WireFrameKind : byte
{
    DirectRatchet = 0x01,
    GroupBroadcast = 0x02,
    X3dhHandshake = 0x03
}
