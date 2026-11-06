using System;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Shell;

internal enum SignalCategory
{
    PendingHandshakeChanged,
    ChatTranscriptChanged,
    RatchetSessionChanged,
    RelayQueueChanged,
    HostedPreKeyCountChanged,
    ProfileChanged,
    GroupStateChanged,
    SwarmProgressChanged
}

internal sealed record SimulatorStateSignal(
    SimulatedNodeId NodeId,
    SignalCategory Category,
    string? Details = null,
    DateTimeOffset TimestampUtc = default)
{
    public DateTimeOffset TimestampUtc { get; init; } = TimestampUtc == default ? DateTimeOffset.UtcNow : TimestampUtc;
}
