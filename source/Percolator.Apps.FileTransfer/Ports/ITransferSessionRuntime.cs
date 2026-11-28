using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Ports;

public sealed record TransferSessionRuntimeOptions(
    TimeSpan? SeedingTtl = null,
    TimeSpan? TeardownTimeout = null,
    int MaxPeersPerSession = 16,
    TimeSpan? CheckpointInterval = null,
    int CheckpointChunkBatchSize = 16,
    int MaxConcurrentIoWorkers = 6,
    int OutboundPeerQueueCapacity = 64)
{
    public TimeSpan EffectiveSeedingTtl => SeedingTtl ?? TimeSpan.FromHours(24);
    public TimeSpan EffectiveTeardownTimeout => TeardownTimeout ?? TimeSpan.FromMilliseconds(750);
    public TimeSpan EffectiveCheckpointInterval => CheckpointInterval ?? TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Session-scoped singleton runtime owning active file transfer sessions, E2E-pinned peer certificates,
/// WebRTC/TransferPipe peer connections, chunk pump tasks, Group V2 eviction enforcement, offline-author
/// seeding while unlocked, and atomic vault-lock teardown within 1 second.
/// </summary>
public interface ITransferSessionRuntime : IAsyncDisposable
{
    int ActiveSessionCount { get; }

    bool HasActiveSession(TransferSessionId sessionId);

    TransferSession? GetActiveSession(TransferSessionId sessionId);

    CertSha256Fingerprint? GetPinnedPeerCertificate(TransferSessionId sessionId, PublicIdentityId peerId);

    IReadOnlyCollection<PublicIdentityId> GetConnectedPeers(TransferSessionId sessionId);

    ValueTask StartOrUpdateSessionAsync(
        TransferSession session,
        TransferSessionTransportContext transportContext,
        RootDirectoryPath localRootPath,
        StunEndpointDescriptor? exchangeStunEndpoint = null,
        bool exchangeSupportsTransferPipe = false,
        RelayEndpoint? exchangeEndpoint = null,
        CancellationToken ct = default);

    ValueTask RegisterPinnedPeerCertificateAsync(
        TransferSessionId sessionId,
        PublicIdentityId peerId,
        CertSha256Fingerprint certSha256,
        EphemeralSwarmPeerId? ephemeralPeerId = null,
        CancellationToken ct = default);

    ValueTask<DomainResult> HandleInboundIceSignalAsync(
        TransferSessionId sessionId,
        PublicIdentityId senderId,
        FileTransferIceSignalPayload signal,
        CancellationToken ct = default);

    ValueTask<DomainResult> InitiateDirectConnectionAsync(
        TransferSessionId sessionId,
        PublicIdentityId remotePeerId,
        CancellationToken ct = default);

    ValueTask<DomainResult> ConnectViaTransferPipeAsync(
        TransferSessionId sessionId,
        PublicIdentityId remotePeerId,
        RelayEndpoint exchangeEndpoint,
        PipeRendezvousToken rendezvousToken,
        bool isTlsServer,
        CancellationToken ct = default);

    ValueTask<DomainResult> AttachAuthenticatedChannelAsync(
        TransferSessionId sessionId,
        PublicIdentityId remotePeerId,
        ITransferDataChannel channel,
        CancellationToken ct = default);

    ValueTask RevokePeerInChannelAsync(
        ChannelId groupChannelId,
        PublicIdentityId evictedPeerId,
        CancellationToken ct = default);

    ValueTask<int> EvaluateSeedingRetentionAsync(CancellationToken ct = default);

    ValueTask StopSessionAsync(
        TransferSessionId sessionId,
        bool deleteCertificate = false,
        CancellationToken ct = default);

    ValueTask StopAllSessionsAsync(CancellationToken ct = default);
}
