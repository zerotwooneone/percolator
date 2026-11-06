using System;
using Percolator.Domain.Identities.ValueObjects;
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

internal sealed record HostedPreKeyCountPayload(PublicIdentityId OwnerId, int AvailableCount);

internal sealed record HandshakeSignalPayload(PublicIdentityId RemotePeerId, string? Action = null);

internal sealed record ChatTranscriptSignalPayload(PublicIdentityId RemotePeerId);

internal sealed record SimulatorStateSignal(
    SimulatedNodeId NodeId,
    SignalCategory Category,
    string? Details = null,
    DateTimeOffset TimestampUtc = default,
    object? Payload = null)
{
    public DateTimeOffset TimestampUtc { get; init; } = TimestampUtc == default ? DateTimeOffset.UtcNow : TimestampUtc;
}
