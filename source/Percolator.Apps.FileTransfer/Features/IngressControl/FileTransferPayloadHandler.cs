using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public sealed class FileTransferPayloadHandler : IAppPayloadHandler
{
    private readonly ITransferSessionRepository _sessionRepository;
    private readonly IFileTransferFilterPolicyRepository _policyRepository;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _clock;
    private readonly IBlobStorageService _blobStorage;
    private readonly ITransferSessionRuntime _runtime;
    private readonly IBackgroundPayloadDispatcher _payloadDispatcher;
    private readonly ITransferSessionCertificateStore _certificateStore;

    public AppId TargetAppId => AppId.FileTransferControl;

    public FileTransferPayloadHandler(
        ITransferSessionRepository sessionRepository,
        IFileTransferFilterPolicyRepository policyRepository,
        IPayloadSerializer serializer,
        IDateTimeProvider clock,
        IBlobStorageService blobStorage,
        ITransferSessionRuntime runtime,
        IBackgroundPayloadDispatcher payloadDispatcher,
        ITransferSessionCertificateStore certificateStore)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _policyRepository = policyRepository ?? throw new ArgumentNullException(nameof(policyRepository));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _blobStorage = blobStorage ?? throw new ArgumentNullException(nameof(blobStorage));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _payloadDispatcher = payloadDispatcher ?? throw new ArgumentNullException(nameof(payloadDispatcher));
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
    }

    public async ValueTask<DomainResult> HandleInboundAsync(InboundPayloadContext context, CancellationToken ct = default)
    {
        if (context.Payload.Length < 1)
            return DomainResult.Failure(new DomainError("Payload.Empty", "Inbound payload was empty."));

        byte opcodeByte = context.Payload.Span[0];
        ReadOnlyMemory<byte> body = context.Payload.Slice(1);

        FileTransferFilterPolicy policy = await _policyRepository.GetPolicyAsync(context.SenderIdentityId, ct);
        if (policy.EvaluatePeer(context.SenderIdentityId) == PeerFilterDecision.Blocked)
            return DomainResult.Success(); // Silent drop

        return opcodeByte switch
        {
            (byte)FileTransferControlOpcode.Offer => await HandleOfferAsync(context.ChannelId, context.RecipientIdentityId, context.SenderIdentityId, body, policy, ct),
            (byte)FileTransferControlOpcode.Accept => await HandleAcceptAsync(context.ChannelId, context.SenderIdentityId, body, ct),
            (byte)FileTransferControlOpcode.Cancel => await HandleCancelAsync(context.SenderIdentityId, body, ct),
            (byte)FileTransferControlOpcode.PeerJoin => await HandlePeerJoinAsync(context.SenderIdentityId, body, ct),
            (byte)FileTransferControlOpcode.IceSignal => await HandleIceSignalAsync(context.ChannelId, context.RecipientIdentityId, context.SenderIdentityId, body, ct),
            _ => DomainResult.Failure(new DomainError("Payload.UnknownOpcode", $"Unknown control opcode 0x{opcodeByte:X2}."))
        };
    }

    private async ValueTask<DomainResult> HandleOfferAsync(
        ChannelId channelId,
        PublicIdentityId localRecipientId,
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        FileTransferFilterPolicy policy,
        CancellationToken ct)
    {
        DomainResult<FileTransferOfferPayload> offerResult = _serializer.Deserialize<FileTransferOfferPayload>(body);
        if (offerResult.IsFailure)
            return DomainResult.Success(); // Silent discard on malformed offer wire format

        FileTransferOfferPayload offer = offerResult.Value;

        ValidationResult validation = IngressTransferOfferValidator.ValidateOffer(offer);
        if (!validation.IsValid)
            return DomainResult.Success(); // Silent discard on malformed offer bounds

        if (offer.RelayRendezvous != null && policy.EvaluateRelay(offer.RelayRendezvous.EndpointUri) == RelayFilterDecision.Blocked)
            return DomainResult.Success(); // Silent drop on blocked relay

        TransferManifest? manifest = await ResolveManifestAsync(offer, ct);
        if (manifest == null)
            return DomainResult.Success(); // Silent discard on invalid or oversized manifest

        TransferSession session = TransferSession.CreateIncomingStaged(
            offer.TransferSessionId,
            senderId,
            manifest,
            SeedRatio.Unlimited,
            _clock);

        IReadOnlyList<DirectEndpointCandidate> filteredCandidates = PexRateLimiter.FilterAndDeduplicate(
            offer.DirectCandidates,
            DirectWanPolicy.DisabledByDefault);

        var transportContext = new TransferSessionTransportContext(
            offer.TransferSessionId,
            channelId,
            localRecipientId,
            offer.SwarmRendezvousId,
            EphemeralSwarmPeerId.Generate(),
            offer.StreamAuthToken,
            DirectWanPolicy.DisabledByDefault,
            offer.RelayRendezvous,
            filteredCandidates,
            LocalRootPath: null,
            IsGroupTransfer: offer.IsGroupTransfer,
            SenderCertSha256: offer.SenderCertSha256);

        await _sessionRepository.SaveSessionAsync(session, ct);
        await _sessionRepository.SaveTransportContextAsync(transportContext, ct);
        return DomainResult.Success(); // Zero wire ACK emitted
    }

    private async ValueTask<TransferManifest?> ResolveManifestAsync(FileTransferOfferPayload offer, CancellationToken ct)
    {
        if (offer.Manifest.Kind == ManifestDistributionKind.Inline && offer.Manifest.InlineData != null)
        {
            DomainResult<TransferManifest> manifestResult = _serializer.Deserialize<TransferManifest>(offer.Manifest.InlineData.SerializedBytes);
            return manifestResult.IsSuccess ? manifestResult.Value : null;
        }

        if (offer.Manifest.Kind == ManifestDistributionKind.RemoteBlob &&
            offer.Manifest.BlobDescriptor != null)
        {
            ManifestBlobDescriptor blob = offer.Manifest.BlobDescriptor;
            var reference = new BlobReference
            {
                Id = blob.BlobId,
                CiphertextSha256 = blob.CiphertextSha256.Span.ToArray(),
                EncryptionKey = blob.BlobEncryptionKey ?? new byte[32],
                BaseNonce = blob.BlobBaseNonce ?? new byte[12],
                PlaintextSizeBytes = blob.PlaintextSizeBytes.Bytes,
                CiphertextSizeBytes = blob.PlaintextSizeBytes.Bytes + 16,
                MimeType = "application/x-percolator-manifest",
                ExpiresAtUtc = _clock.UtcNow.AddDays(1),
                StorageMode = BlobStorageMode.ChannelRelay,
                RelayEndpointUri = blob.RelayEndpointUri.Value
            };

            DomainResult<BlobDownloadResult> downloadResult = await _blobStorage.DownloadBlobAsync(new BlobDownloadRequest(reference), ct: ct);
            if (downloadResult.IsFailure)
                return null;

            await using Stream contentStream = downloadResult.Value.ContentStream;
            using MemoryStream boundedBuffer = new();
            byte[] readChunk = new byte[8192];
            long totalRead = 0;

            while (true)
            {
                int read = await contentStream.ReadAsync(readChunk, ct);
                if (read <= 0) break;

                totalRead += read;
                if (totalRead > IngressTransferOfferValidator.MaxRemoteManifestBlobBytes)
                    return null; // Abort deceptive overflow beyond 8 MB cap

                boundedBuffer.Write(readChunk, 0, read);
            }

            DomainResult<TransferManifest> manifestResult = _serializer.Deserialize<TransferManifest>(boundedBuffer.ToArray());
            return manifestResult.IsSuccess ? manifestResult.Value : null;
        }

        return null;
    }

    private async ValueTask<DomainResult> HandleAcceptAsync(
        ChannelId channelId,
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        DomainResult<FileTransferAcceptPayload> acceptResult = _serializer.Deserialize<FileTransferAcceptPayload>(body);
        if (acceptResult.IsFailure)
            return DomainResult.Failure(acceptResult.Error);

        FileTransferAcceptPayload accept = acceptResult.Value;

        TransferSession? session = _runtime.GetActiveSession(accept.TransferSessionId)
            ?? await _sessionRepository.GetSessionByIdAsync(accept.TransferSessionId, ct);
        if (session == null)
            return DomainResult.Failure(new DomainError("Transfer.NotFound", "Transfer session was not found."));

        DomainResult result = session.RecordPeerAcceptance(senderId, accept.ResumptionBitfield, _clock);
        if (result.IsFailure)
            return result;

        await _sessionRepository.SaveSessionAsync(session, ct);

        if (accept.ReceiverCertSha256 != null)
        {
            await _runtime.RegisterPinnedPeerCertificateAsync(
                accept.TransferSessionId,
                senderId,
                accept.ReceiverCertSha256,
                ct: ct);

            if (_runtime.HasActiveSession(accept.TransferSessionId))
            {
                TransferSessionTransportContext? transportContext = await _sessionRepository.GetTransportContextAsync(accept.TransferSessionId, ct);
                if (transportContext != null)
                {
                    if (transportContext.LocalRootPath != null)
                    {
                        await _runtime.StartOrUpdateSessionAsync(
                            session,
                            transportContext,
                            transportContext.LocalRootPath,
                            ct: ct);
                    }

                    await _runtime.InitiateDirectConnectionAsync(
                        accept.TransferSessionId,
                        senderId,
                        CreateIceSignalDispatcher(
                            _serializer,
                            _payloadDispatcher,
                            transportContext.ChannelId,
                            transportContext.LocalIdentityId,
                            senderId,
                            transportContext.IsGroupTransfer),
                        ct);
                }
            }
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandlePeerJoinAsync(
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        DomainResult<FileTransferPeerJoinPayload> joinResult = _serializer.Deserialize<FileTransferPeerJoinPayload>(body);
        if (joinResult.IsFailure)
            return DomainResult.Success(); // Silent drop on malformed PeerJoin

        FileTransferPeerJoinPayload join = joinResult.Value;

        TransferSession? session = _runtime.GetActiveSession(join.TransferSessionId)
            ?? await _sessionRepository.GetSessionByIdAsync(join.TransferSessionId, ct);
        TransferSessionTransportContext? transportContext = await _sessionRepository.GetTransportContextAsync(join.TransferSessionId, ct);
        if (session == null || transportContext == null || !transportContext.IsGroupTransfer)
            return DomainResult.Success(); // Ignore PeerJoin for unknown or non-group sessions

        if (senderId == transportContext.LocalIdentityId)
            return DomainResult.Success();

        bool wasAlreadyPinned = _runtime.GetPinnedPeerCertificate(join.TransferSessionId, senderId) != null;

        DomainResult authResult = session.AuthorizeGroupPeer(senderId);
        if (authResult.IsFailure)
            return authResult;

        if (session.State == TransferSessionState.Offered)
        {
            session.RecordPeerAcceptance(senderId, Bitfield.Empty(session.Manifest.TotalChunks), _clock);
        }

        await _sessionRepository.SaveSessionAsync(session, ct);
        await _runtime.RegisterPinnedPeerCertificateAsync(
            join.TransferSessionId,
            senderId,
            join.CertSha256,
            join.EphemeralPeerId,
            ct);

        if (_runtime.HasActiveSession(join.TransferSessionId) && !wasAlreadyPinned)
        {
            if (transportContext.LocalRootPath != null)
            {
                await _runtime.StartOrUpdateSessionAsync(
                    session,
                    transportContext,
                    transportContext.LocalRootPath,
                    ct: ct);
            }

            // Reply once with our own PeerJoin over Group V2 so late-joining peers (including after
            // the original author goes offline) pin our certificate fingerprint before our SdpOffer arrives.
            using TransferSessionCertificateMaterial? localCert = await _certificateStore.GetAsync(join.TransferSessionId, ct);
            if (localCert != null)
            {
                FileTransferPeerJoinPayload replyJoin = new(
                    join.TransferSessionId,
                    transportContext.EphemeralPeerId,
                    localCert.Fingerprint);

                ReadOnlyMemory<byte> replySerialized = _serializer.Serialize(replyJoin);
                byte[] replyFramed = new byte[1 + replySerialized.Length];
                replyFramed[0] = (byte)FileTransferControlOpcode.PeerJoin;
                replySerialized.Span.CopyTo(replyFramed.AsSpan(1));

                await _payloadDispatcher.DispatchAsync(
                    new OutboundPayloadContext(
                        transportContext.ChannelId,
                        transportContext.LocalIdentityId,
                        RecipientIdentityId: null,
                        DeviceId.Primary,
                        AppId.FileTransferControl,
                        replyFramed,
                        DeliveryRoute.RelayedGroup),
                    ct);
            }

            // If we have verified chunks (initial author or unlocked seeder), initiate WebRTC directly to the joining peer.
            if (session.LocalBitfield.PopCount > 0)
            {
                await _runtime.InitiateDirectConnectionAsync(
                    join.TransferSessionId,
                    senderId,
                    CreateIceSignalDispatcher(
                        _serializer,
                        _payloadDispatcher,
                        transportContext.ChannelId,
                        transportContext.LocalIdentityId,
                        senderId,
                        isGroupTransfer: true),
                    ct);
            }
        }

        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleIceSignalAsync(
        ChannelId channelId,
        PublicIdentityId localIdentityId,
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        DomainResult<FileTransferIceSignalPayload> signalResult = _serializer.Deserialize<FileTransferIceSignalPayload>(body);
        if (signalResult.IsFailure)
            return DomainResult.Failure(signalResult.Error);

        FileTransferIceSignalPayload signal = signalResult.Value;

        // Group V2 targeted ICE signals are broadcast on the group SenderKey channel; ignore signals addressed to another peer.
        if (signal.TargetPeerId.HasValue && signal.TargetPeerId.Value != localIdentityId)
            return DomainResult.Success();

        if (senderId == localIdentityId)
            return DomainResult.Success();

        TransferSessionTransportContext? transportContext = await _sessionRepository.GetTransportContextAsync(signal.TransferSessionId, ct);
        ChannelId replyChannelId = transportContext?.ChannelId ?? channelId;
        PublicIdentityId replyLocalId = transportContext?.LocalIdentityId ?? localIdentityId;
        bool isGroupTransfer = transportContext?.IsGroupTransfer ?? false;

        return await _runtime.HandleInboundIceSignalAsync(
            signal.TransferSessionId,
            senderId,
            signal,
            CreateIceSignalDispatcher(
                _serializer,
                _payloadDispatcher,
                replyChannelId,
                replyLocalId,
                senderId,
                isGroupTransfer),
            ct);
    }

    private async ValueTask<DomainResult> HandleCancelAsync(
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        DomainResult<FileTransferCancelPayload> cancelResult = _serializer.Deserialize<FileTransferCancelPayload>(body);
        if (cancelResult.IsFailure)
            return DomainResult.Success(); // Silent drop for malformed cancel frame

        FileTransferCancelPayload cancel = cancelResult.Value;

        TransferSession? session = _runtime.GetActiveSession(cancel.TransferSessionId)
            ?? await _sessionRepository.GetSessionByIdAsync(cancel.TransferSessionId, ct);
        if (session == null || !session.Topology.ContainsParticipant(senderId))
            return DomainResult.Success(); // Silent drop for unknown or unauthorized sender

        DomainResult result = session.Cancel(cancel.Reason, _clock);
        if (result.IsSuccess)
        {
            await _sessionRepository.SaveSessionAsync(session, ct);
            await _runtime.StopSessionAsync(cancel.TransferSessionId, deleteCertificate: true, ct);
        }

        return DomainResult.Success();
    }

    private static Func<FileTransferIceSignalPayload, CancellationToken, ValueTask> CreateIceSignalDispatcher(
        IPayloadSerializer serializer,
        IBackgroundPayloadDispatcher dispatcher,
        ChannelId channelId,
        PublicIdentityId localIdentityId,
        PublicIdentityId remotePeerId,
        bool isGroupTransfer)
    {
        // Capture ONLY singleton dependencies + value types (never the scoped FileTransferPayloadHandler)
        // so background trickle ICE candidates remain valid after the inbound DI scope closes.
        return async (signalPayload, signalCt) =>
        {
            FileTransferIceSignalPayload wirePayload = isGroupTransfer
                ? signalPayload with { TargetPeerId = remotePeerId }
                : signalPayload;

            ReadOnlyMemory<byte> serialized = serializer.Serialize(wirePayload);
            byte[] framed = new byte[1 + serialized.Length];
            framed[0] = (byte)FileTransferControlOpcode.IceSignal;
            serialized.Span.CopyTo(framed.AsSpan(1));

            var outboundContext = isGroupTransfer
                ? new OutboundPayloadContext(
                    channelId,
                    localIdentityId,
                    RecipientIdentityId: null,
                    DeviceId.Primary,
                    AppId.FileTransferControl,
                    framed,
                    DeliveryRoute.RelayedGroup)
                : new OutboundPayloadContext(
                    channelId,
                    localIdentityId,
                    remotePeerId,
                    DeviceId.Primary,
                    AppId.FileTransferControl,
                    framed,
                    DeliveryRoute.RelayedOneToOne);

            await dispatcher.DispatchAsync(outboundContext, signalCt).ConfigureAwait(false);
        };
    }
}
