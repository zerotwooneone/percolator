using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed class RelayPersistenceDto
{
    public int Version { get; set; } = 1;
    public PeerId RelayHostPeerId { get; set; }

    public List<RelayUpstreamMessageDto> UpstreamToMain { get; set; } = new();

    public List<RelayDownstreamMessageDto> DownstreamToPeers { get; set; } = new();
}

public sealed class RelayUpstreamMessageDto
{
    public Guid AckId { get; set; }
    public byte[] OpaqueBytes { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedUtc { get; set; }
    public string? DebugType { get; set; }
}

public sealed class RelayDownstreamMessageDto
{
    public Guid AckId { get; set; }
    public byte[] TargetPkh { get; set; } = Array.Empty<byte>();
    public byte[] OpaqueBytes { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedUtc { get; set; }
    public string? DebugType { get; set; }
}
