using System.Collections.Concurrent;
using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.PluginSdk.Events;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Features.Runtime;

public sealed class TransferSessionRuntime : ITransferSessionRuntime
{
    private readonly ITransferPeerTransport _peerTransport;
    private readonly ITransferSessionCertificateStore _certificateStore;
    private readonly IDistributedTrackerPort _trackerPort;
    private readonly IDesktopFileStorage _fileStorage;
    private readonly ITransferRateLimiter _rateLimiter;
    private readonly IDateTimeProvider _clock;
    private readonly TransferSessionRuntimeOptions _options;
    private readonly IDisposable _memberRemovedSubscription;

    private readonly ConcurrentDictionary<TransferSessionId, ActiveTransferHandle> _sessions = new();
    private int _disposed;

    public int ActiveSessionCount => _sessions.Count;

    public TransferSessionRuntime(
        ITransferPeerTransport peerTransport,
        ITransferSessionCertificateStore certificateStore,
        IDistributedTrackerPort trackerPort,
        IDesktopFileStorage fileStorage,
        ITransferRateLimiter rateLimiter,
        IDateTimeProvider clock,
        IEventSubscriber eventSubscriber,
        TransferSessionRuntimeOptions options)
    {
        _peerTransport = peerTransport ?? throw new ArgumentNullException(nameof(peerTransport));
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
        _trackerPort = trackerPort ?? throw new ArgumentNullException(nameof(trackerPort));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(eventSubscriber);
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _memberRemovedSubscription = eventSubscriber.Subscribe<GroupMemberRemovedApplicationEvent>(OnGroupMemberRemovedAsync);
    }

    public bool HasActiveSession(TransferSessionId sessionId) => _sessions.ContainsKey(sessionId);

    public TransferSession? GetActiveSession(TransferSessionId sessionId) =>
        _sessions.TryGetValue(sessionId, out ActiveTransferHandle? handle) ? handle.Session : null;

    public CertSha256Fingerprint? GetPinnedPeerCertificate(TransferSessionId sessionId, PublicIdentityId peerId) =>
        _sessions.TryGetValue(sessionId, out ActiveTransferHandle? handle) &&
        handle.PinnedPeerCerts.TryGetValue(peerId, out CertSha256Fingerprint? cert)
            ? cert
            : null;

    public IReadOnlyCollection<PublicIdentityId> GetConnectedPeers(TransferSessionId sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out ActiveTransferHandle? handle))
        {
            return [];
        }

        return handle.PeerChannels.Keys.ToArray();
    }

    public ValueTask StartOrUpdateSessionAsync(
        TransferSession session,
        TransferSessionTransportContext transportContext,
        RootDirectoryPath localRootPath,
        StunEndpointDescriptor? exchangeStunEndpoint = null,
        bool exchangeSupportsTransferPipe = false,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transportContext);
        ArgumentNullException.ThrowIfNull(localRootPath);

        _sessions.AddOrUpdate(
            session.Id,
            _ => CreateHandle(session, transportContext, localRootPath, exchangeStunEndpoint, exchangeSupportsTransferPipe),
            (_, existing) =>
            {
                lock (existing.SyncRoot)
                {
                    existing.Session = session;
                    existing.TransportContext = transportContext;
                    existing.LocalRootPath = localRootPath;
                    if (exchangeStunEndpoint != null)
                    {
                        existing.ExchangeStunEndpoint = exchangeStunEndpoint;
                    }
                    if (exchangeSupportsTransferPipe)
                    {
                        existing.ExchangeSupportsTransferPipe = true;
                    }
                    if (session.State == TransferSessionState.Seeding && existing.SeedingStartedAtUtc == null)
                    {
                        existing.SeedingStartedAtUtc = _clock.UtcNow;
                    }
                }

                if (transportContext.SenderCertSha256 != null &&
                    session.InitiatorId != transportContext.LocalIdentityId)
                {
                    existing.PinnedPeerCerts[session.InitiatorId] = transportContext.SenderCertSha256;
                }

                return existing;
            });

        return ValueTask.CompletedTask;
    }

    public ValueTask RegisterPinnedPeerCertificateAsync(
        TransferSessionId sessionId,
        PublicIdentityId peerId,
        CertSha256Fingerprint certSha256,
        EphemeralSwarmPeerId? ephemeralPeerId = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(certSha256);

        if (_sessions.TryGetValue(sessionId, out ActiveTransferHandle? handle))
        {
            lock (handle.SyncRoot)
            {
                if (handle.TransportContext.IsGroupTransfer &&
                    !handle.Session.Topology.ContainsParticipant(peerId))
                {
                    handle.Session.AuthorizeGroupPeer(peerId);
                }
            }

            handle.PinnedPeerCerts[peerId] = certSha256;
            if (ephemeralPeerId != null)
            {
                handle.PeerEphemeralIds[peerId] = ephemeralPeerId;
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<DomainResult> HandleInboundIceSignalAsync(
        TransferSessionId sessionId,
        PublicIdentityId senderId,
        FileTransferIceSignalPayload signal,
        Func<FileTransferIceSignalPayload, CancellationToken, ValueTask> sendSignalReplyAsync,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(sendSignalReplyAsync);

        if (!_sessions.TryGetValue(sessionId, out ActiveTransferHandle? handle))
        {
            return DomainResult.Failure(new DomainError("Transfer.SessionNotActive", "Transfer session is not active in the runtime."));
        }

        bool isAuthorized;
        lock (handle.SyncRoot)
        {
            isAuthorized = handle.Session.Topology.ContainsParticipant(senderId);
        }

        if (!isAuthorized || !handle.PinnedPeerCerts.TryGetValue(senderId, out CertSha256Fingerprint? pinnedCert))
        {
            return DomainResult.Failure(new DomainError("Transfer.UnauthorizedOrRevokedPeer", "Peer is not authorized or its certificate has been revoked."));
        }

        if (signal.DtlsFingerprintSha256 != null &&
            !CryptographicOperations.FixedTimeEquals(signal.DtlsFingerprintSha256.Span, pinnedCert.Span))
        {
            return DomainResult.Failure(new DomainError(
                TransferTransportErrorCodes.DtlsFingerprintMismatch,
                "Inbound ICE signal DTLS fingerprint does not match the E2E-pinned peer certificate."));
        }

        switch (signal.Kind)
        {
            case IceSignalKind.SdpOffer:
            {
                if (handle.PeerConnections.Count >= _options.MaxPeersPerSession &&
                    !handle.PeerConnections.ContainsKey(senderId))
                {
                    return DomainResult.Failure(new DomainError(
                        TransferTransportErrorCodes.SessionPeerCapExceeded,
                        $"Session reached maximum peer cap ({_options.MaxPeersPerSession})."));
                }

                var connOptions = new DirectPeerConnectionOptions(
                    sessionId,
                    senderId,
                    IsInitiator: false,
                    ExchangeStunEndpoint: handle.ExchangeStunEndpoint,
                    MaxPeersPerSession: _options.MaxPeersPerSession);

                ITransferPeerConnection connection = await _peerTransport.CreateDirectConnectionAsync(connOptions, ct).ConfigureAwait(false);
                if (handle.PeerConnections.TryGetValue(senderId, out ITransferPeerConnection? previous))
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                handle.PeerConnections[senderId] = connection;
                WireIceCandidateForwarder(handle, connection, sendSignalReplyAsync);

                string answerSdp = await connection.AcceptOfferAndCreateAnswerAsync(
                    signal.SdpOrCandidate,
                    pinnedCert,
                    ct).ConfigureAwait(false);

                var answerPayload = new FileTransferIceSignalPayload(
                    sessionId,
                    IceSignalKind.SdpAnswer,
                    answerSdp,
                    CandidateMid: null,
                    DtlsFingerprintSha256: connection.LocalDtlsFingerprint);

                await sendSignalReplyAsync(answerPayload, ct).ConfigureAwait(false);
                SpawnChannelAttachTask(handle, senderId, connection);
                return DomainResult.Success();
            }

            case IceSignalKind.SdpAnswer:
            {
                if (!handle.PeerConnections.TryGetValue(senderId, out ITransferPeerConnection? connection))
                {
                    return DomainResult.Failure(new DomainError("Transfer.ConnectionNotFound", "No outbound peer connection awaits an SDP answer for this peer."));
                }

                await connection.ApplyRemoteAnswerAsync(signal.SdpOrCandidate, pinnedCert, ct).ConfigureAwait(false);
                return DomainResult.Success();
            }

            case IceSignalKind.IceCandidate:
            {
                if (!handle.PeerConnections.TryGetValue(senderId, out ITransferPeerConnection? connection))
                {
                    return DomainResult.Failure(new DomainError("Transfer.ConnectionNotFound", "No active peer connection exists to receive an ICE candidate."));
                }

                await connection.AddRemoteIceCandidateAsync(
                    signal.SdpOrCandidate,
                    signal.CandidateMid ?? "0",
                    ct).ConfigureAwait(false);

                return DomainResult.Success();
            }

            case IceSignalKind.PipeRendezvous:
            {
                if (signal.PipeToken == null || signal.PipeEndpointUri == null)
                {
                    return DomainResult.Failure(new DomainError("Transfer.InvalidPipeRendezvous", "PipeRendezvous signal requires both PipeToken and PipeEndpointUri."));
                }

                RelayEndpoint exchangeEndpoint = RelayEndpoint.Parse(signal.PipeEndpointUri.Value);
                return await ConnectViaTransferPipeAsync(
                    sessionId,
                    senderId,
                    exchangeEndpoint,
                    signal.PipeToken,
                    isTlsServer: false,
                    ct).ConfigureAwait(false);
            }

            default:
                return DomainResult.Failure(new DomainError("Transfer.UnsupportedIceSignal", $"Unsupported ICE signal kind '{signal.Kind}'."));
        }
    }

    public async ValueTask<DomainResult> InitiateDirectConnectionAsync(
        TransferSessionId sessionId,
        PublicIdentityId remotePeerId,
        Func<FileTransferIceSignalPayload, CancellationToken, ValueTask> sendSignalAsync,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sendSignalAsync);

        if (!_sessions.TryGetValue(sessionId, out ActiveTransferHandle? handle))
        {
            return DomainResult.Failure(new DomainError("Transfer.SessionNotActive", "Transfer session is not active in the runtime."));
        }

        if (!handle.PinnedPeerCerts.ContainsKey(remotePeerId))
        {
            return DomainResult.Failure(new DomainError("Transfer.UnpinnedPeer", "Cannot initiate direct connection without an E2E-pinned peer certificate."));
        }

        var connOptions = new DirectPeerConnectionOptions(
            sessionId,
            remotePeerId,
            IsInitiator: true,
            ExchangeStunEndpoint: handle.ExchangeStunEndpoint,
            MaxPeersPerSession: _options.MaxPeersPerSession);

        ITransferPeerConnection connection = await _peerTransport.CreateDirectConnectionAsync(connOptions, ct).ConfigureAwait(false);
        if (handle.PeerConnections.TryGetValue(remotePeerId, out ITransferPeerConnection? previous))
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        handle.PeerConnections[remotePeerId] = connection;
        WireIceCandidateForwarder(handle, connection, sendSignalAsync);

        string offerSdp = await connection.CreateOfferAsync(ct).ConfigureAwait(false);
        var offerPayload = new FileTransferIceSignalPayload(
            sessionId,
            IceSignalKind.SdpOffer,
            offerSdp,
            CandidateMid: null,
            DtlsFingerprintSha256: connection.LocalDtlsFingerprint);

        await sendSignalAsync(offerPayload, ct).ConfigureAwait(false);
        SpawnChannelAttachTask(handle, remotePeerId, connection);
        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> ConnectViaTransferPipeAsync(
        TransferSessionId sessionId,
        PublicIdentityId remotePeerId,
        RelayEndpoint exchangeEndpoint,
        PipeRendezvousToken rendezvousToken,
        bool isTlsServer,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(exchangeEndpoint);
        ArgumentNullException.ThrowIfNull(rendezvousToken);

        if (!_sessions.TryGetValue(sessionId, out ActiveTransferHandle? handle))
        {
            return DomainResult.Failure(new DomainError("Transfer.SessionNotActive", "Transfer session is not active in the runtime."));
        }

        if (!handle.PinnedPeerCerts.TryGetValue(remotePeerId, out CertSha256Fingerprint? expectedCert))
        {
            return DomainResult.Failure(new DomainError("Transfer.UnpinnedPeer", "Cannot open relayed TransferPipe without an E2E-pinned peer certificate."));
        }

        var pipeOptions = new RelayedPipeConnectionOptions(
            sessionId,
            handle.TransportContext.LocalIdentityId,
            remotePeerId,
            exchangeEndpoint,
            rendezvousToken,
            handle.TransportContext.StreamAuthToken,
            isTlsServer,
            expectedCert,
            ExchangeSupportsTransferPipe: handle.ExchangeSupportsTransferPipe,
            MaxPeersPerSession: _options.MaxPeersPerSession);

        try
        {
            ITransferDataChannel channel = await _peerTransport.OpenRelayedPipeChannelAsync(pipeOptions, ct).ConfigureAwait(false);
            return await AttachAuthenticatedChannelAsync(sessionId, remotePeerId, channel, ct).ConfigureAwait(false);
        }
        catch (TransferTransportException ex)
        {
            return DomainResult.Failure(new DomainError(ex.ErrorCode, ex.Message));
        }
    }

    public async ValueTask<DomainResult> AttachAuthenticatedChannelAsync(
        TransferSessionId sessionId,
        PublicIdentityId remotePeerId,
        ITransferDataChannel channel,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(channel);

        if (!_sessions.TryGetValue(sessionId, out ActiveTransferHandle? handle))
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return DomainResult.Failure(new DomainError("Transfer.SessionNotActive", "Transfer session is not active in the runtime."));
        }

        if (!handle.PinnedPeerCerts.TryGetValue(remotePeerId, out CertSha256Fingerprint? expectedCert))
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return DomainResult.Failure(new DomainError("Transfer.UnpinnedPeer", "Peer certificate is not pinned or has been revoked."));
        }

        if (!CryptographicOperations.FixedTimeEquals(channel.VerifiedRemoteCertSha256.Span, expectedCert.Span))
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return DomainResult.Failure(new DomainError(
                TransferTransportErrorCodes.DtlsFingerprintMismatch,
                "Channel peer certificate SHA-256 fingerprint does not match the E2E-pinned certificate."));
        }

        lock (handle.SyncRoot)
        {
            if (!handle.Session.Topology.ContainsParticipant(remotePeerId))
            {
                return DomainResult.Failure(new DomainError("Swarm.UnauthorizedPeer", "Peer is not an authorized participant in this transfer session."));
            }

            if (!handle.Session.SwarmMembers.Any(m => m.PeerId == remotePeerId))
            {
                handle.Session.RegisterSwarmMember(
                    remotePeerId,
                    Bitfield.Empty(handle.Session.Manifest.TotalChunks),
                    _clock,
                    peerChokingUs: PeerChokeStatus.Unchoked,
                    weChokingPeer: PeerChokeStatus.Unchoked);
            }
        }

        handle.SwarmCoordinator.RegisterPeer(remotePeerId);

        if (handle.PeerChannels.TryGetValue(remotePeerId, out ITransferDataChannel? existingChannel))
        {
            await existingChannel.DisposeAsync().ConfigureAwait(false);
        }

        handle.PeerChannels[remotePeerId] = channel;

        Task pumpTask = Task.Run(() => PumpChannelAsync(handle, remotePeerId, channel, handle.SessionCts.Token), CancellationToken.None);
        handle.PeerPumpTasks[remotePeerId] = pumpTask;
        return DomainResult.Success();
    }

    public async ValueTask RevokePeerInChannelAsync(
        ChannelId groupChannelId,
        PublicIdentityId evictedPeerId,
        CancellationToken ct = default)
    {
        foreach (ActiveTransferHandle handle in _sessions.Values)
        {
            if (handle.TransportContext.ChannelId != groupChannelId)
            {
                continue;
            }

            handle.PinnedPeerCerts.TryRemove(evictedPeerId, out _);
            handle.PeerEphemeralIds.TryRemove(evictedPeerId, out _);

            lock (handle.SyncRoot)
            {
                handle.Session.RevokeGroupPeer(evictedPeerId);
            }

            handle.Coordinator.Scheduler.ReleasePeer(evictedPeerId);

            if (handle.PeerChannels.TryRemove(evictedPeerId, out ITransferDataChannel? channel))
            {
                try
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (handle.PeerConnections.TryRemove(evictedPeerId, out ITransferPeerConnection? connection))
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    public async ValueTask<int> EvaluateSeedingRetentionAsync(CancellationToken ct = default)
    {
        int stoppedCount = 0;
        List<TransferSessionId> expiredSessions = [];

        foreach ((TransferSessionId sessionId, ActiveTransferHandle handle) in _sessions)
        {
            TransferSession session;
            DateTimeOffset seedingStartedAt;
            TransferSessionTransportContext transportContext;

            lock (handle.SyncRoot)
            {
                session = handle.Session;
                transportContext = handle.TransportContext;
                if (session.State != TransferSessionState.Seeding &&
                    session.State != TransferSessionState.Completed)
                {
                    continue;
                }

                seedingStartedAt = handle.SeedingStartedAtUtc ?? session.CreatedAtUtc;
            }

            bool stopped = await handle.SwarmCoordinator.EvaluateSeedingRetentionAsync(
                session,
                seedingStartedAt,
                _options.EffectiveSeedingTtl,
                _trackerPort,
                _peerTransport,
                transportContext.SwarmRendezvousId,
                transportContext.EphemeralPeerId,
                manualStopRequested: false,
                ct).ConfigureAwait(false);

            if (stopped)
            {
                expiredSessions.Add(sessionId);
            }
        }

        foreach (TransferSessionId sessionId in expiredSessions)
        {
            await StopSessionInternalAsync(sessionId, unregisterTracker: false, deleteCertificate: false, ct).ConfigureAwait(false);
            stoppedCount++;
        }

        return stoppedCount;
    }

    public ValueTask StopSessionAsync(
        TransferSessionId sessionId,
        bool deleteCertificate = false,
        CancellationToken ct = default) =>
        StopSessionInternalAsync(sessionId, unregisterTracker: true, deleteCertificate, ct);

    public async ValueTask StopAllSessionsAsync(CancellationToken ct = default)
    {
        using var teardownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        teardownCts.CancelAfter(_options.EffectiveTeardownTimeout);
        CancellationToken boundedCt = teardownCts.Token;

        KeyValuePair<TransferSessionId, ActiveTransferHandle>[] snapshot = _sessions.ToArray();
        _sessions.Clear();

        List<Task> pumpTasks = [];
        List<Task> unregisterTasks = [];

        foreach ((TransferSessionId _, ActiveTransferHandle handle) in snapshot)
        {
            try
            {
                handle.SessionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            pumpTasks.AddRange(handle.PeerPumpTasks.Values);

            TransferSessionTransportContext ctx = handle.TransportContext;
            unregisterTasks.Add(UnregisterBestEffortAsync(ctx.SwarmRendezvousId, ctx.EphemeralPeerId, boundedCt));

            foreach (ITransferDataChannel channel in handle.PeerChannels.Values)
            {
                try
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            foreach (ITransferPeerConnection connection in handle.PeerConnections.Values)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            handle.PeerChannels.Clear();
            handle.PeerConnections.Clear();
            handle.SessionCts.Dispose();
        }

        try
        {
            await _peerTransport.CloseAllConnectionsAsync(boundedCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (unregisterTasks.Count > 0)
        {
            await WaitBoundedAsync(Task.WhenAll(unregisterTasks), boundedCt).ConfigureAwait(false);
        }

        if (pumpTasks.Count > 0)
        {
            await WaitBoundedAsync(Task.WhenAll(pumpTasks), boundedCt).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _memberRemovedSubscription.Dispose();
        await StopAllSessionsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private ValueTask OnGroupMemberRemovedAsync(GroupMemberRemovedApplicationEvent ev, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return RevokePeerInChannelAsync(ev.ChannelId, ev.MemberId, ct);
    }

    private ActiveTransferHandle CreateHandle(
        TransferSession session,
        TransferSessionTransportContext transportContext,
        RootDirectoryPath localRootPath,
        StunEndpointDescriptor? exchangeStunEndpoint,
        bool exchangeSupportsTransferPipe)
    {
        var scheduler = new ChunkPipelineScheduler();
        var assembler = new DiskChunkAssembler(_fileStorage);
        var coordinator = new TransferSessionCoordinator(scheduler, assembler, _clock, _rateLimiter);
        var swarmCoordinator = new SwarmCoordinator(_clock);

        var handle = new ActiveTransferHandle(
            session,
            transportContext,
            localRootPath,
            coordinator,
            swarmCoordinator,
            exchangeStunEndpoint,
            exchangeSupportsTransferPipe,
            session.State == TransferSessionState.Seeding ? _clock.UtcNow : null);

        if (transportContext.SenderCertSha256 != null &&
            session.InitiatorId != transportContext.LocalIdentityId)
        {
            handle.PinnedPeerCerts[session.InitiatorId] = transportContext.SenderCertSha256;
        }

        return handle;
    }

    private void WireIceCandidateForwarder(
        ActiveTransferHandle handle,
        ITransferPeerConnection connection,
        Func<FileTransferIceSignalPayload, CancellationToken, ValueTask> sendSignalAsync)
    {
        connection.SignalProduced += evt =>
        {
            if (handle.SessionCts.IsCancellationRequested || evt.Kind != IceSignalKind.IceCandidate)
            {
                return;
            }

            var payload = new FileTransferIceSignalPayload(
                evt.SessionId,
                evt.Kind,
                evt.SdpOrCandidate,
                evt.CandidateMid,
                evt.LocalDtlsFingerprint);

            _ = Task.Run(async () =>
            {
                try
                {
                    await sendSignalAsync(payload, handle.SessionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None);
        };
    }

    private void SpawnChannelAttachTask(
        ActiveTransferHandle handle,
        PublicIdentityId remotePeerId,
        ITransferPeerConnection connection)
    {
        Task attachTask = Task.Run(async () =>
        {
            try
            {
                ITransferDataChannel channel = await connection.WaitForAuthenticatedChannelAsync(
                    handle.TransportContext.StreamAuthToken,
                    handle.TransportContext.LocalIdentityId,
                    handle.SessionCts.Token).ConfigureAwait(false);

                await AttachAuthenticatedChannelAsync(
                    handle.Session.Id,
                    remotePeerId,
                    channel,
                    handle.SessionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        handle.PeerPumpTasks.TryAdd(remotePeerId, attachTask);
    }

    private async Task PumpChannelAsync(
        ActiveTransferHandle handle,
        PublicIdentityId remotePeerId,
        ITransferDataChannel channel,
        CancellationToken ct)
    {
        try
        {
            Bitfield initialBitfield;
            bool hasAnyChunks;
            lock (handle.SyncRoot)
            {
                initialBitfield = handle.Session.LocalBitfield;
                hasAnyChunks = initialBitfield.PopCount > 0;
            }

            await channel.SendFrameAsync(new BitfieldFrame(initialBitfield), ct).ConfigureAwait(false);
            if (hasAnyChunks)
            {
                await channel.SendFrameAsync(new UnchokeFrame(), ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested)
            {
                bool isTerminal;
                lock (handle.SyncRoot)
                {
                    isTerminal = handle.Session.State is TransferSessionState.Completed
                        or TransferSessionState.Cancelled
                        or TransferSessionState.Failed;
                }

                if (isTerminal)
                {
                    break;
                }

                await ScheduleAndRequestMissingChunksAsync(handle, remotePeerId, channel, ct).ConfigureAwait(false);

                IStreamFramePayload frame;
                try
                {
                    frame = await channel.ReceiveFrameAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (System.Threading.Channels.ChannelClosedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                handle.SwarmCoordinator.RecordPeerActivity(remotePeerId);

                switch (frame)
                {
                    case BitfieldFrame bitfieldFrame:
                    {
                        bool peerHasNeeded;
                        lock (handle.SyncRoot)
                        {
                            handle.Session.RecordPeerBitfield(remotePeerId, bitfieldFrame.Bitfield);
                            SwarmMember? member = handle.Session.SwarmMembers.FirstOrDefault(m => m.PeerId == remotePeerId);
                            peerHasNeeded = member != null && member.WeInterestedInPeer == PeerInterestStatus.Interested;
                        }

                        if (peerHasNeeded)
                        {
                            await channel.SendFrameAsync(new InterestedFrame(), ct).ConfigureAwait(false);
                        }
                        break;
                    }

                    case HaveFrame haveFrame:
                    {
                        lock (handle.SyncRoot)
                        {
                            handle.Session.RecordPeerHave(remotePeerId, haveFrame.ChunkIndex);
                        }
                        break;
                    }

                    case InterestedFrame:
                    {
                        lock (handle.SyncRoot)
                        {
                            handle.Session.RecordPeerInterestedInUs(remotePeerId, PeerInterestStatus.Interested);
                            handle.Session.SetWeChokingPeer(remotePeerId, PeerChokeStatus.Unchoked);
                        }

                        await channel.SendFrameAsync(new UnchokeFrame(), ct).ConfigureAwait(false);
                        break;
                    }

                    case NotInterestedFrame:
                    {
                        lock (handle.SyncRoot)
                        {
                            handle.Session.RecordPeerInterestedInUs(remotePeerId, PeerInterestStatus.NotInterested);
                        }
                        break;
                    }

                    case UnchokeFrame:
                    {
                        lock (handle.SyncRoot)
                        {
                            handle.Session.RecordPeerChokingUs(remotePeerId, PeerChokeStatus.Unchoked);
                        }
                        break;
                    }

                    case ChokeFrame:
                    {
                        lock (handle.SyncRoot)
                        {
                            handle.Session.RecordPeerChokingUs(remotePeerId, PeerChokeStatus.Choked);
                        }

                        handle.Coordinator.Scheduler.ReleasePeer(remotePeerId);
                        break;
                    }

                    case CancelChunkFrame cancelFrame:
                    {
                        handle.Coordinator.Scheduler.ReleaseChunk(cancelFrame.ChunkIndex);
                        break;
                    }

                    case RequestChunkFrame reqFrame:
                    {
                        bool canServe;
                        lock (handle.SyncRoot)
                        {
                            SwarmMember? member = handle.Session.SwarmMembers.FirstOrDefault(m => m.PeerId == remotePeerId);
                            canServe = member == null || member.WeChokingPeer != PeerChokeStatus.Choked;
                        }

                        if (canServe)
                        {
                            await handle.Coordinator.HandleRequestChunkAsync(
                                handle.Session,
                                remotePeerId,
                                reqFrame,
                                channel,
                                _fileStorage,
                                handle.LocalRootPath,
                                ct).ConfigureAwait(false);
                        }
                        break;
                    }

                    case ChunkDataFrame chunkData:
                    {
                        bool wasSeedingBefore;
                        lock (handle.SyncRoot)
                        {
                            wasSeedingBefore = handle.Session.State == TransferSessionState.Seeding;
                        }

                        DomainResult result = await handle.Coordinator.HandleIncomingChunkDataAsync(
                            handle.Session,
                            remotePeerId,
                            chunkData,
                            channel,
                            handle.LocalRootPath,
                            streamPool: null,
                            ct).ConfigureAwait(false);

                        if (result.IsFailure)
                        {
                            if (result.Error.Code == "MultiStream.CorruptedChunk")
                            {
                                return;
                            }
                            break;
                        }

                        await BroadcastHaveAsync(handle, chunkData.ChunkIndex, ct).ConfigureAwait(false);

                        bool transitionedToSeeding = false;
                        lock (handle.SyncRoot)
                        {
                            if (!wasSeedingBefore && handle.Session.State == TransferSessionState.Seeding)
                            {
                                handle.SeedingStartedAtUtc ??= _clock.UtcNow;
                                transitionedToSeeding = true;
                            }
                        }

                        if (transitionedToSeeding)
                        {
                            await AnnounceSeederBestEffortAsync(
                                handle.TransportContext.SwarmRendezvousId,
                                handle.TransportContext.EphemeralPeerId,
                                ct).ConfigureAwait(false);
                        }
                        break;
                    }

                    case KeepAliveFrame:
                        break;
                }
            }
        }
        finally
        {
            handle.Coordinator.Scheduler.ReleasePeer(remotePeerId);
            handle.PeerChannels.TryRemove(remotePeerId, out _);
            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async ValueTask ScheduleAndRequestMissingChunksAsync(
        ActiveTransferHandle handle,
        PublicIdentityId remotePeerId,
        ITransferDataChannel channel,
        CancellationToken ct)
    {
        while (handle.Coordinator.Scheduler.CanRequest(remotePeerId))
        {
            ChunkIndex? nextChunk;
            lock (handle.SyncRoot)
            {
                if (handle.Session.IsDownloadComplete ||
                    handle.Session.State != TransferSessionState.InFlight)
                {
                    nextChunk = null;
                }
                else
                {
                    Bitfield required = handle.Session.SelectedFiles != null
                        ? handle.Session.Manifest.GetRequiredChunks(handle.Session.SelectedFiles)
                        : Bitfield.Full(handle.Session.Manifest.TotalChunks);

                    nextChunk = RarestFirstChunkSelector.SelectNextChunkForPeer(
                        remotePeerId,
                        handle.Session.LocalBitfield,
                        required,
                        handle.Session.SwarmMembers,
                        handle.Coordinator.Scheduler.InFlightChunks);

                    if (nextChunk.HasValue && !handle.Coordinator.Scheduler.TrySchedule(remotePeerId, nextChunk.Value))
                    {
                        nextChunk = null;
                    }
                }
            }

            if (!nextChunk.HasValue)
            {
                break;
            }

            await channel.SendFrameAsync(new RequestChunkFrame(nextChunk.Value), ct).ConfigureAwait(false);
        }
    }

    private static async ValueTask BroadcastHaveAsync(
        ActiveTransferHandle handle,
        ChunkIndex chunkIndex,
        CancellationToken ct)
    {
        var haveFrame = new HaveFrame(chunkIndex);
        foreach (ITransferDataChannel peerChannel in handle.PeerChannels.Values)
        {
            if (!peerChannel.IsOpen)
            {
                continue;
            }

            try
            {
                await peerChannel.SendFrameAsync(haveFrame, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Individual peer channel may have closed concurrently; continue broadcasting to remaining peers.
            }
        }
    }

    private async ValueTask StopSessionInternalAsync(
        TransferSessionId sessionId,
        bool unregisterTracker,
        bool deleteCertificate,
        CancellationToken ct)
    {
        using var teardownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        teardownCts.CancelAfter(_options.EffectiveTeardownTimeout);
        CancellationToken boundedCt = teardownCts.Token;

        if (_sessions.TryRemove(sessionId, out ActiveTransferHandle? handle))
        {
            try
            {
                handle.SessionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            foreach (ITransferDataChannel channel in handle.PeerChannels.Values)
            {
                try
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            foreach (ITransferPeerConnection connection in handle.PeerConnections.Values)
            {
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            handle.PeerChannels.Clear();
            handle.PeerConnections.Clear();

            if (unregisterTracker)
            {
                await UnregisterBestEffortAsync(
                    handle.TransportContext.SwarmRendezvousId,
                    handle.TransportContext.EphemeralPeerId,
                    boundedCt).ConfigureAwait(false);
            }

            if (handle.PeerPumpTasks.Count > 0)
            {
                await WaitBoundedAsync(Task.WhenAll(handle.PeerPumpTasks.Values), boundedCt).ConfigureAwait(false);
            }

            handle.SessionCts.Dispose();
        }

        try
        {
            await _peerTransport.CloseSessionConnectionsAsync(sessionId, boundedCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (deleteCertificate)
        {
            await _certificateStore.DeleteAsync(sessionId, ct).ConfigureAwait(false);
        }
    }

    private async Task UnregisterBestEffortAsync(
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        CancellationToken ct)
    {
        try
        {
            await _trackerPort.UnregisterAsync(rendezvousId, ephemeralPeerId, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Tracker unreachable during teardown must not block socket/vault teardown.
        }
    }

    private async ValueTask AnnounceSeederBestEffortAsync(
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        CancellationToken ct)
    {
        try
        {
            await _trackerPort.AnnounceAsync(rendezvousId, ephemeralPeerId, SwarmPeerRole.Seeder, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private static async Task WaitBoundedAsync(Task task, CancellationToken boundedCt)
    {
        try
        {
            await task.WaitAsync(boundedCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TransferSessionRuntime));
        }
    }

    private sealed class ActiveTransferHandle
    {
        public object SyncRoot { get; } = new();
        public TransferSession Session { get; set; }
        public TransferSessionTransportContext TransportContext { get; set; }
        public RootDirectoryPath LocalRootPath { get; set; }
        public TransferSessionCoordinator Coordinator { get; }
        public SwarmCoordinator SwarmCoordinator { get; }
        public CancellationTokenSource SessionCts { get; } = new();
        public StunEndpointDescriptor? ExchangeStunEndpoint { get; set; }
        public bool ExchangeSupportsTransferPipe { get; set; }
        public DateTimeOffset? SeedingStartedAtUtc { get; set; }

        public ConcurrentDictionary<PublicIdentityId, CertSha256Fingerprint> PinnedPeerCerts { get; } = new();
        public ConcurrentDictionary<PublicIdentityId, EphemeralSwarmPeerId> PeerEphemeralIds { get; } = new();
        public ConcurrentDictionary<PublicIdentityId, ITransferPeerConnection> PeerConnections { get; } = new();
        public ConcurrentDictionary<PublicIdentityId, ITransferDataChannel> PeerChannels { get; } = new();
        public ConcurrentDictionary<PublicIdentityId, Task> PeerPumpTasks { get; } = new();

        public ActiveTransferHandle(
            TransferSession session,
            TransferSessionTransportContext transportContext,
            RootDirectoryPath localRootPath,
            TransferSessionCoordinator coordinator,
            SwarmCoordinator swarmCoordinator,
            StunEndpointDescriptor? exchangeStunEndpoint,
            bool exchangeSupportsTransferPipe,
            DateTimeOffset? seedingStartedAtUtc)
        {
            Session = session;
            TransportContext = transportContext;
            LocalRootPath = localRootPath;
            Coordinator = coordinator;
            SwarmCoordinator = swarmCoordinator;
            ExchangeStunEndpoint = exchangeStunEndpoint;
            ExchangeSupportsTransferPipe = exchangeSupportsTransferPipe;
            SeedingStartedAtUtc = seedingStartedAtUtc;
        }
    }
}
