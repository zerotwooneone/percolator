using System;
using Percolator.Domain.Channels.ValueObjects;
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
    SwarmProgressChanged,
    DiscoveryStateChanged,
    FileTransferOfferReceived,
    FileTransferCompleted
}

internal sealed record HostedPreKeyCountPayload(PublicIdentityId OwnerId, int AvailableCount);

internal sealed record HandshakeSignalPayload(PublicIdentityId RemotePeerId, string? Action = null);

internal sealed record ChatTranscriptSignalPayload(PublicIdentityId RemotePeerId);

internal sealed record FileTransferOfferSignalPayload(
    Guid SessionId,
    PublicIdentityId SenderId,
    string BundleTitle,
    long TotalBytes,
    int TotalChunks);

internal sealed record FileTransferProgressSignalPayload(
    Guid SessionId,
    PublicIdentityId PeerId,
    int CompletedChunks,
    int TotalChunks,
    long DownloadedBytes,
    long TotalBytes);

internal sealed record FileTransferCompletedSignalPayload(
    Guid SessionId,
    PublicIdentityId PeerId,
    string FilePath,
    string Sha256Hash);

internal sealed record SimulatorStateSignal(
    SimulatedNodeId NodeId,
    SignalCategory Category,
    string? Details = null,
    DateTimeOffset TimestampUtc = default,
    object? Payload = null,
    ChannelId? TargetChannelId = null)
{
    public DateTimeOffset TimestampUtc { get; init; } = TimestampUtc == default ? DateTimeOffset.UtcNow : TimestampUtc;
}
