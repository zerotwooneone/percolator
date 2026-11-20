namespace Percolator.Apps.FileTransfer.Features.MultiStreamEngine;

public enum StreamFrameType : byte
{
    Handshake = 0x01,
    Bitfield = 0x02,
    Have = 0x03,
    RequestChunk = 0x04,
    ChunkData = 0x05,
    CancelChunk = 0x06,
    Choke = 0x07,
    Unchoke = 0x08,
    Interested = 0x09,
    NotInterested = 0x0A,
    KeepAlive = 0x0B,
    PexPeers = 0x0C
}
