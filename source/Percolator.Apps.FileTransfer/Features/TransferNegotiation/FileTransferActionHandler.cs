using System.Buffers;
using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed class FileTransferActionHandler : IFileTransferActionHandler
{
    private readonly ITransferSessionRepository _sessionRepository;
    private readonly IDesktopFileStorage _fileStorage;
    private readonly IPayloadSender _payloadSender;
    private readonly IPayloadSerializer _serializer;
    private readonly IDistributedTrackerPort _trackerPort;
    private readonly IDateTimeProvider _clock;
    private readonly IBlobStorageService? _blobStorage;
    private readonly INetworkEndpointDiscoveryPort? _endpointDiscovery;
    private readonly ITransferStreamPool? _streamPool;

    public FileTransferActionHandler(
        ITransferSessionRepository sessionRepository,
        IDesktopFileStorage fileStorage,
        IPayloadSender payloadSender,
        IPayloadSerializer serializer,
        IDistributedTrackerPort trackerPort,
        IDateTimeProvider clock,
        IBlobStorageService? blobStorage = null,
        INetworkEndpointDiscoveryPort? endpointDiscovery = null,
        ITransferStreamPool? streamPool = null)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _trackerPort = trackerPort ?? throw new ArgumentNullException(nameof(trackerPort));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _blobStorage = blobStorage;
        _endpointDiscovery = endpointDiscovery;
        _streamPool = streamPool;
    }

    public ValueTask<DomainResult<TransferSessionId>> InitiateTransferAsync(
        IReadOnlyList<PublicIdentityId> recipients,
        RootDirectoryPath sourceRoot,
        IReadOnlyList<SanitizedRelativePath> filePaths,
        DirectWanPolicy wanPolicy,
        RootBundleName? customBundleName = null,
        SeedRatio? targetSeedRatio = null,
        CancellationToken ct = default)
    {
        // Derive a deterministic local initiator distinct from all recipients when not explicitly provided
        PublicIdentityId defaultInitiator = CreateDistinctLocalInitiator(recipients);
        ChannelId defaultChannelId = ChannelId.New();

        return InitiateTransferAsync(
            defaultChannelId,
            defaultInitiator,
            recipients,
            sourceRoot,
            filePaths,
            wanPolicy,
            customBundleName,
            targetSeedRatio,
            ct);
    }

    public async ValueTask<DomainResult<TransferSessionId>> InitiateTransferAsync(
        ChannelId channelId,
        PublicIdentityId localInitiator,
        IReadOnlyList<PublicIdentityId> recipients,
        RootDirectoryPath sourceRoot,
        IReadOnlyList<SanitizedRelativePath> filePaths,
        DirectWanPolicy wanPolicy,
        RootBundleName? customBundleName = null,
        SeedRatio? targetSeedRatio = null,
        CancellationToken ct = default)
    {
        if (recipients == null || recipients.Count == 0)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.NoRecipients", "Must specify at least one recipient."));

        if (filePaths == null || filePaths.Count == 0)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.NoFiles", "Must specify at least one file to transfer."));

        if (filePaths.Count > IngressTransferOfferValidator.MaxFilesPerBundle)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.FileLimitExceeded", $"File count ({filePaths.Count}) exceeds maximum allowed ({IngressTransferOfferValidator.MaxFilesPerBundle})."));

        // Scan files via IDesktopFileStorage and build contiguous FileEntries
        List<FileEntry> entries = new(filePaths.Count);
        long cumulativeOffset = 0;
        for (int i = 0; i < filePaths.Count; i++)
        {
            SanitizedRelativePath relPath = filePaths[i];
            ResolvedFilePath absPath = ResolvedFilePath.Resolve(sourceRoot, relPath);
            FileMetadataResult metadata = await _fileStorage.GetFileMetadataAsync(absPath, ct);
            if (!metadata.Exists)
                return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.FileNotFound", $"File not found: '{relPath.Value}'."));

            BundleByteOffset startOffset = BundleByteOffset.FromOffset(cumulativeOffset);
            entries.Add(new FileEntry(FileIndex.FromInt(i), relPath, metadata.Size, startOffset, metadata.LastModifiedUtc));
            cumulativeOffset += metadata.Size.Bytes;
        }

        if (cumulativeOffset > IngressTransferOfferValidator.MaxBundleSizeBytes)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.SizeLimitExceeded", "Bundle total size exceeds 16 TB."));

        TotalBundleBytes totalBundleBytes = TotalBundleBytes.FromBytes(cumulativeOffset);
        ChunkSizeBytes chunkSize = ChunkSizeBytes.SelectOptimalForBundle(totalBundleBytes);
        int chunkCount = (int)Math.Max(1, (cumulativeOffset + chunkSize.Bytes - 1) / chunkSize.Bytes);
        if (chunkCount > IngressTransferOfferValidator.MaxChunksPerBundle)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.ChunkLimitExceeded", "Bundle total chunk count exceeds 1,000,000."));

        // Build leaf hashes and Merkle tree using pooled buffers (zero LOH thrashing)
        List<ChunkHash> leafHashes = new(chunkCount);
        byte[] rentedChunkBuffer = ArrayPool<byte>.Shared.Rent(chunkSize.Bytes);
        try
        {
            for (int c = 0; c < chunkCount; c++)
            {
                ChunkIndex chunkIdx = ChunkIndex.FromInt(c);
                int expectedBytes = (int)Math.Min((long)chunkSize.Bytes, cumulativeOffset - ((long)c * chunkSize.Bytes));
                ChunkByteLength expectedSize = ChunkByteLength.FromBytes(expectedBytes);

                Span<byte> activeSpan = rentedChunkBuffer.AsSpan(0, expectedBytes);
                activeSpan.Clear();

                IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlicesFromEntries(entries, chunkSize, chunkIdx, expectedSize);
                foreach (FileChunkSlice slice in slices)
                {
                    FileEntry entry = entries[slice.FileIndex.Value];
                    ResolvedFilePath filePath = ResolvedFilePath.Resolve(sourceRoot, entry.RelativePath);
                    PlaintextChunkData sliceData = await _fileStorage.ReadChunkAsync(filePath, slice.OffsetInFile, slice.SliceLength, ct);
                    sliceData.Span.CopyTo(rentedChunkBuffer.AsSpan(slice.BufferOffset, slice.SliceLength.Bytes));
                }

                leafHashes.Add(MerkleTreeBuilder.HashLeaf(rentedChunkBuffer.AsSpan(0, expectedBytes)));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedChunkBuffer, clearArray: true);
        }

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves(leafHashes);
        TransferSessionId sessionId = TransferSessionId.New();
        string fallbackName = Path.GetFileName(sourceRoot.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fallbackName)) fallbackName = "Transfer";
        RootBundleName bundleTitle = customBundleName ?? RootBundleName.Parse(fallbackName);
        TransferManifest manifest = TransferManifest.Create(sessionId, bundleTitle, chunkSize, merkleTree, entries);

        TransferKey sessionKey = TransferKey.Generate();
        BaseNonce baseNonce = BaseNonce.Generate();
        StreamAuthToken streamAuthToken = StreamAuthToken.Generate();
        SeedRatio enforcedSeedRatio = targetSeedRatio ?? SeedRatio.Zero;

        TransferSession session = recipients.Count == 1
            ? TransferSession.CreateOutgoing1to1(sessionId, localInitiator, recipients[0], manifest, enforcedSeedRatio, _clock)
            : TransferSession.CreateOutgoingMultiPeer(sessionId, localInitiator, recipients, manifest, enforcedSeedRatio, _clock);

        IReadOnlyList<DirectEndpointCandidate> rawCandidates = _endpointDiscovery != null
            ? await _endpointDiscovery.DiscoverLocalCandidatesAsync(wanPolicy, ct)
            : [];
        IReadOnlyList<DirectEndpointCandidate> candidates = PexRateLimiter.FilterAndDeduplicate(rawCandidates, wanPolicy);

        RelayRendezvousDescriptor? relayRendezvous = _endpointDiscovery != null
            ? await _endpointDiscovery.AllocateRelayRendezvousAsync(sessionId, ct)
            : null;

        // Announce as Seeder to the swarm tracker (BitTorrent swarm bootstrap)
        SwarmRendezvousId rendezvousId = SwarmRendezvousId.Derive(sessionKey);
        await _trackerPort.AnnounceAsync(rendezvousId, localInitiator, candidates, SwarmPeerRole.Seeder, ct);

        var transportContext = new TransferSessionTransportContext(
            sessionId,
            channelId,
            localInitiator,
            sessionKey,
            baseNonce,
            streamAuthToken,
            wanPolicy,
            relayRendezvous,
            candidates,
            sourceRoot);

        await _sessionRepository.SaveSessionAsync(session, ct);
        await _sessionRepository.SaveTransportContextAsync(transportContext, ct);

        ReadOnlyMemory<byte> serializedManifest = _serializer.Serialize(manifest);
        ManifestDistribution manifestDist;
        if (serializedManifest.Length <= IngressTransferOfferValidator.MaxInlineManifestBytes)
        {
            manifestDist = ManifestDistribution.FromInline(InlineManifestData.FromSpan(serializedManifest.Span));
        }
        else if (_blobStorage != null)
        {
            using MemoryStream manifestStream = new(serializedManifest.ToArray(), writable: false);
            var uploadRequest = new BlobUploadRequest(
                manifestStream,
                "application/x-percolator-manifest",
                serializedManifest.Length,
                channelId);

            DomainResult<BlobUploadResult> uploadResult = await _blobStorage.UploadBlobAsync(uploadRequest, ct: ct);
            if (uploadResult.IsFailure)
                return DomainResult<TransferSessionId>.Failure(uploadResult.Error);

            BlobReference blobRef = uploadResult.Value.Reference;
            manifestDist = ManifestDistribution.FromBlob(new ManifestBlobDescriptor(
                blobRef.Id,
                ChunkHash.FromBytes(blobRef.CiphertextSha256),
                TotalBundleBytes.FromBytes(serializedManifest.Length),
                RelayPipeEndpointUri.Parse(blobRef.RelayEndpointUri ?? "https://relay.percolator.net/pipes")));
        }
        else
        {
            byte[] sha256 = SHA256.HashData(serializedManifest.Span);
            manifestDist = ManifestDistribution.FromBlob(new ManifestBlobDescriptor(
                BlobId.FromSha256(sha256),
                ChunkHash.FromBytes(sha256),
                TotalBundleBytes.FromBytes(serializedManifest.Length),
                RelayPipeEndpointUri.Parse("https://relay.percolator.net/pipes")));
        }

        FileTransferOfferPayload offerPayload = new(
            sessionId,
            TransferBundleSummary.Create(bundleTitle, manifest.TotalSize, manifest.TotalFiles, chunkSize, manifest.TotalChunks, merkleTree.Root),
            manifestDist,
            sessionKey,
            baseNonce,
            streamAuthToken,
            relayRendezvous,
            candidates);

        byte[] inBandBytes = PrependOpcode(FileTransferControlOpcode.Offer, _serializer.Serialize(offerPayload).Span);

        foreach (PublicIdentityId recipient in recipients)
        {
            var outboundContext = new OutboundPayloadContext(
                channelId,
                localInitiator,
                recipient,
                DeviceId.Primary,
                AppId.FileTransferControl,
                inBandBytes,
                DeliveryRoute.DirectP2P);

            await _payloadSender.SendPayloadAsync(outboundContext, ct);
        }

        return DomainResult<TransferSessionId>.Success(sessionId);
    }

    public async ValueTask<DomainResult> AcceptTransferAsync(
        TransferSessionId sessionId,
        RootDirectoryPath destinationFolder,
        SelectedFileIndices? selectedFiles = null,
        CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null)
            return DomainResult.Failure(new DomainError("Transfer.NotFound", "Transfer session was not found."));

        if (session.State != TransferSessionState.PendingConsent)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot accept transfer in state '{session.State}'."));

        SelectedFileIndices chosenFiles = selectedFiles ?? SelectedFileIndices.All(session.Manifest.TotalFiles);
        if (chosenFiles.SelectedCount == 0)
            return DomainResult.Failure(new DomainError("Transfer.EmptySelection", "At least one file must be selected."));

        // Selective Disk Space Pre-flight Check
        long requiredBytes = 0;
        foreach (FileIndex idx in chosenFiles.Indices)
        {
            requiredBytes += session.Manifest.Files[idx.Value].FileSize.Bytes;
        }

        DiskSpacePreflightResult diskCheck = await _fileStorage.CheckDiskSpaceAsync(
            destinationFolder,
            TotalBundleBytes.FromBytes(requiredBytes),
            ct);

        if (!diskCheck.HasSufficientSpace)
            return DomainResult.Failure(new DomainError("Storage.InsufficientSpace", $"Insufficient disk space. Required: {requiredBytes} bytes."));

        // Scan existing partial files on disk to compute resumption bitfield (croc-style fast resumption)
        Bitfield resumptionBitfield = await _fileStorage.ScanAndComputeBitfieldAsync(
            destinationFolder,
            session.Manifest,
            chosenFiles,
            ct);

        DomainResult acceptResult = session.AcceptTransfer(chosenFiles, _clock, resumptionBitfield);
        if (acceptResult.IsFailure)
            return acceptResult;

        // Register sender in local swarm roster with full bitfield so we can schedule chunks from sender
        session.RegisterSwarmMember(session.InitiatorId, Bitfield.Full(session.Manifest.TotalChunks), _clock);
        session.RecordPeerChokingUs(session.InitiatorId, PeerChokeStatus.Unchoked);
        session.SetWeChokingPeer(session.InitiatorId, PeerChokeStatus.Unchoked);

        // Immediate Finalization for Zero-Byte or Already-Verified Resumed Files
        foreach (FileIndex idx in chosenFiles.Indices)
        {
            FileEntry file = session.Manifest.Files[idx.Value];
            if (file.FileSize.Bytes == 0 || session.IsFileComplete(file))
            {
                ResolvedFilePath path = ResolvedFilePath.Resolve(destinationFolder, file.RelativePath);
                await _fileStorage.FinalizeFileAsync(path, file.LastModifiedUtc, ct);
            }
        }

        // Pre-allocate remaining non-empty files
        await _fileStorage.PreAllocateFilesAsync(destinationFolder, session.Manifest, chosenFiles, ct);
        await _sessionRepository.SaveSessionAsync(session, ct);

        TransferSessionTransportContext? transportContext = await _sessionRepository.GetTransportContextAsync(sessionId, ct);
        DirectWanPolicy wanPolicy = transportContext?.WanPolicy ?? DirectWanPolicy.DisabledByDefault;

        IReadOnlyList<DirectEndpointCandidate> rawReceiverCandidates = _endpointDiscovery != null
            ? await _endpointDiscovery.DiscoverLocalCandidatesAsync(wanPolicy, ct)
            : [];
        IReadOnlyList<DirectEndpointCandidate> receiverCandidates = PexRateLimiter.FilterAndDeduplicate(rawReceiverCandidates, wanPolicy);

        ChannelId channelId = transportContext?.ChannelId ?? ChannelId.FromGuid(sessionId.Value);
        PublicIdentityId localSenderId = transportContext?.LocalIdentityId ?? CreateDistinctLocalInitiator([session.InitiatorId]);

        if (transportContext != null)
        {
            await _sessionRepository.SaveTransportContextAsync(
                transportContext with { LocalRootPath = destinationFolder },
                ct);

            // Announce to distributed swarm tracker and merge any discovered swarm peer candidates
            SwarmRendezvousId rendezvousId = SwarmRendezvousId.Derive(transportContext.SessionEncryptionKey);
            SwarmPeerRole role = session.IsDownloadComplete ? SwarmPeerRole.Seeder : SwarmPeerRole.Leecher;
            IReadOnlyList<DirectEndpointCandidate> trackerCandidates = await _trackerPort.AnnounceAsync(
                rendezvousId,
                localSenderId,
                receiverCandidates,
                role,
                ct);

            // Establish out-of-band multi-stream connection (croc-style direct P2P with blind relay fallback)
            if (_streamPool != null && !session.IsDownloadComplete)
            {
                List<DirectEndpointCandidate> combinedDirect = [.. transportContext.DirectCandidates];
                foreach (var tc in PexRateLimiter.FilterAndDeduplicate(trackerCandidates, wanPolicy, receiverCandidates))
                {
                    if (!combinedDirect.Contains(tc))
                        combinedDirect.Add(tc);
                }

                await ConnectDirectWithRelayFallbackAsync(_streamPool, combinedDirect, transportContext.RelayRendezvous, ct);
            }
        }

        TransportRoutingPreference routingPref = (transportContext?.DirectCandidates.Count > 0 || receiverCandidates.Count > 0)
            ? TransportRoutingPreference.DirectPreferred
            : TransportRoutingPreference.RelayOnly;

        // Send in-band accept payload from local recipient back to session.InitiatorId
        FileTransferAcceptPayload acceptPayload = new(
            sessionId,
            receiverCandidates,
            routingPref,
            session.LocalBitfield);

        byte[] inBandBytes = PrependOpcode(FileTransferControlOpcode.Accept, _serializer.Serialize(acceptPayload).Span);

        var outboundContext = new OutboundPayloadContext(
            channelId,
            localSenderId,
            session.InitiatorId,
            DeviceId.Primary,
            AppId.FileTransferControl,
            inBandBytes,
            DeliveryRoute.DirectP2P);

        await _payloadSender.SendPayloadAsync(outboundContext, ct);

        return DomainResult.Success();
    }

    internal static async ValueTask ConnectDirectWithRelayFallbackAsync(
        ITransferStreamPool streamPool,
        IReadOnlyList<DirectEndpointCandidate> directCandidates,
        RelayRendezvousDescriptor? relayRendezvous,
        CancellationToken ct)
    {
        if (directCandidates.Count > 0)
        {
            try
            {
                await streamPool.ConnectDirectAsync(directCandidates, ct);
                return;
            }
            catch (Exception) when (relayRendezvous != null)
            {
                // Direct P2P failed (symmetric NAT / firewall): fall back to blind relay pipe
            }
        }

        if (relayRendezvous != null)
        {
            await streamPool.ConnectRelayPipeAsync(relayRendezvous, ct);
        }
    }

    public async ValueTask<DomainResult> PauseTransferAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Pause(_clock);
        if (result.IsSuccess) await _sessionRepository.SaveSessionAsync(session, ct);
        return result;
    }

    public async ValueTask<DomainResult> ResumeTransferAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Resume(_clock);
        if (result.IsSuccess) await _sessionRepository.SaveSessionAsync(session, ct);
        return result;
    }

    public async ValueTask<DomainResult> CancelTransferAsync(TransferSessionId sessionId, TransferTerminationReason reason, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Cancel(reason, _clock);
        if (result.IsSuccess)
        {
            await _sessionRepository.SaveSessionAsync(session, ct);

            TransferSessionTransportContext? transportContext = await _sessionRepository.GetTransportContextAsync(sessionId, ct);
            TransferKey sessionKey = transportContext?.SessionEncryptionKey ?? TransferKey.FromBytes(new byte[32]);
            PublicIdentityId localIdentity = transportContext?.LocalIdentityId ?? session.InitiatorId;
            ChannelId channelId = transportContext?.ChannelId ?? ChannelId.FromGuid(sessionId.Value);

            // Proactive tracker unregistration using the actual session TransferKey
            SwarmRendezvousId rendezvous = SwarmRendezvousId.Derive(sessionKey);
            await _trackerPort.UnregisterAsync(rendezvous, localIdentity, ct);

            // Send in-band cancel payload
            FileTransferCancelPayload cancelPayload = new(sessionId, reason);
            byte[] inBandBytes = PrependOpcode(FileTransferControlOpcode.Cancel, _serializer.Serialize(cancelPayload).Span);

            PublicIdentityId targetRecipient = DetermineCancelTarget(session, localIdentity);
            var outboundContext = new OutboundPayloadContext(
                channelId,
                localIdentity,
                targetRecipient,
                DeviceId.Primary,
                AppId.FileTransferControl,
                inBandBytes,
                DeliveryRoute.DirectP2P);

            await _payloadSender.SendPayloadAsync(outboundContext, ct);
        }
        return result;
    }

    private static PublicIdentityId DetermineCancelTarget(TransferSession session, PublicIdentityId localIdentity)
    {
        if (localIdentity != session.InitiatorId)
            return session.InitiatorId;

        return session.Topology switch
        {
            TransferTopology.OneToOne oneToOne => oneToOne.RecipientId,
            TransferTopology.MultiPeer multiPeer => multiPeer.Recipients[0],
            _ => session.InitiatorId
        };
    }

    private static PublicIdentityId CreateDistinctLocalInitiator(IReadOnlyList<PublicIdentityId>? recipients)
    {
        byte[] bytes = new byte[16];
        bytes[0] = 0xFE;
        bytes[15] = 0x01;
        PublicIdentityId candidate = PublicIdentityId.FromBytes(bytes);

        if (recipients != null)
        {
            while (recipients.Contains(candidate))
            {
                bytes[1]++;
                candidate = PublicIdentityId.FromBytes(bytes);
            }
        }

        return candidate;
    }

    private static byte[] PrependOpcode(FileTransferControlOpcode opcode, ReadOnlySpan<byte> payload)
    {
        byte[] framed = new byte[1 + payload.Length];
        framed[0] = (byte)opcode;
        payload.CopyTo(framed.AsSpan(1));
        return framed;
    }
}
