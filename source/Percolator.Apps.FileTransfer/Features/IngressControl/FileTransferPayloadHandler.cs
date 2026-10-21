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

namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public sealed class FileTransferPayloadHandler : IAppPayloadHandler
{
    private readonly ITransferSessionRepository _sessionRepository;
    private readonly IFileTransferFilterPolicyRepository _policyRepository;
    private readonly IPayloadSerializer _serializer;
    private readonly IDateTimeProvider _clock;
    private readonly IBlobStorageService? _blobStorage;
    private readonly ITransferStreamPool? _streamPool;

    public AppId TargetAppId => AppId.FileTransferControl;

    public FileTransferPayloadHandler(
        ITransferSessionRepository sessionRepository,
        IFileTransferFilterPolicyRepository policyRepository,
        IPayloadSerializer serializer,
        IDateTimeProvider clock,
        IBlobStorageService? blobStorage = null,
        ITransferStreamPool? streamPool = null)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _policyRepository = policyRepository ?? throw new ArgumentNullException(nameof(policyRepository));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _blobStorage = blobStorage;
        _streamPool = streamPool;
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
            (byte)FileTransferControlOpcode.Offer => await HandleOfferAsync(context.ChannelId, context.SenderIdentityId, body, policy, ct),
            (byte)FileTransferControlOpcode.Accept => await HandleAcceptAsync(context.SenderIdentityId, body, ct),
            (byte)FileTransferControlOpcode.Cancel => await HandleCancelAsync(context.SenderIdentityId, body, ct),
            _ => DomainResult.Failure(new DomainError("Payload.UnknownOpcode", $"Unknown control opcode 0x{opcodeByte:X2}."))
        };
    }

    private async ValueTask<DomainResult> HandleOfferAsync(
        ChannelId channelId,
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
            SeedRatio.Zero,
            _clock);

        IReadOnlyList<DirectEndpointCandidate> filteredCandidates = PexRateLimiter.FilterAndDeduplicate(
            offer.DirectCandidates,
            DirectWanPolicy.DisabledByDefault);

        var transportContext = new TransferSessionTransportContext(
            offer.TransferSessionId,
            channelId,
            policy.OwnerId,
            offer.SessionEncryptionKey,
            offer.BaseNonce,
            offer.StreamAuthToken,
            DirectWanPolicy.DisabledByDefault,
            offer.RelayRendezvous,
            filteredCandidates);

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
            offer.Manifest.BlobDescriptor != null &&
            _blobStorage != null)
        {
            ManifestBlobDescriptor blob = offer.Manifest.BlobDescriptor;
            var reference = new BlobReference
            {
                Id = blob.BlobId,
                CiphertextSha256 = blob.CiphertextSha256.Span.ToArray(),
                EncryptionKey = offer.SessionEncryptionKey.Span.ToArray(),
                BaseNonce = offer.BaseNonce.Span.ToArray(),
                PlaintextSizeBytes = blob.PlaintextSizeBytes.Bytes,
                CiphertextSizeBytes = blob.PlaintextSizeBytes.Bytes + 16,
                MimeType = "application/x-percolator-manifest",
                ExpiresAtUtc = _clock.UtcNow.AddDays(1),
                StorageMode = BlobStorageMode.ChannelRelay,
                RelayEndpointUri = blob.RelayEndpointUri.Value
            };

            DomainResult<BlobDownloadResult> downloadResult = await _blobStorage.DownloadBlobAsync(new BlobDownloadRequest(reference, ct), ct: ct);
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
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        DomainResult<FileTransferAcceptPayload> acceptResult = _serializer.Deserialize<FileTransferAcceptPayload>(body);
        if (acceptResult.IsFailure)
            return DomainResult.Failure(acceptResult.Error);

        FileTransferAcceptPayload accept = acceptResult.Value;

        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(accept.TransferSessionId, ct);
        if (session == null)
            return DomainResult.Failure(new DomainError("Transfer.NotFound", "Transfer session was not found."));

        DomainResult result = session.RecordPeerAcceptance(senderId, accept.ResumptionBitfield, _clock);
        if (result.IsFailure)
            return result;

        await _sessionRepository.SaveSessionAsync(session, ct);

        if (_streamPool != null)
        {
            TransferSessionTransportContext? transportContext = await _sessionRepository.GetTransportContextAsync(accept.TransferSessionId, ct);
            DirectWanPolicy wanPolicy = transportContext?.WanPolicy ?? DirectWanPolicy.DisabledByDefault;

            IReadOnlyList<DirectEndpointCandidate> filteredCandidates = accept.RoutingPreference == TransportRoutingPreference.RelayOnly
                ? []
                : PexRateLimiter.FilterAndDeduplicate(accept.ReceiverCandidates, wanPolicy, transportContext?.DirectCandidates);

            await FileTransferActionHandler.ConnectDirectWithRelayFallbackAsync(
                _streamPool,
                filteredCandidates,
                transportContext?.RelayRendezvous,
                ct);
        }

        return DomainResult.Success();
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

        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(cancel.TransferSessionId, ct);
        if (session == null || !session.Topology.ContainsParticipant(senderId))
            return DomainResult.Success(); // Silent drop for unknown or unauthorized sender

        DomainResult result = session.Cancel(cancel.Reason, _clock);
        if (result.IsSuccess)
        {
            await _sessionRepository.SaveSessionAsync(session, ct);
        }

        return DomainResult.Success();
    }
}
