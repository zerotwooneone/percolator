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
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;
using INetworkEndpointDiscoveryPort = Percolator.Apps.FileTransfer.Ports.INetworkEndpointDiscoveryPort;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed class FileTransferActionHandler : IFileTransferActionHandler
{
    private readonly ITransferSessionRepository _sessionRepository;
    private readonly IDesktopFileStorage _fileStorage;
    private readonly IPayloadSender _payloadSender;
    private readonly IPayloadSerializer _serializer;
    private readonly IDistributedTrackerPort _trackerPort;
    private readonly IDateTimeProvider _clock;
    private readonly IBlobStorageService _blobStorage;
    private readonly INetworkEndpointDiscoveryPort _endpointDiscovery;
    private readonly ITransferSessionRuntime _runtime;
    private readonly ITransferSessionCertificateStore _certificateStore;
    private readonly FileTransferIngressFilter _ingressFilter;
    private readonly ITransferSessionTransportStore _transportStore;

    public FileTransferActionHandler(
        ITransferSessionRepository sessionRepository,
        IDesktopFileStorage fileStorage,
        IPayloadSender payloadSender,
        IPayloadSerializer serializer,
        IDistributedTrackerPort trackerPort,
        IDateTimeProvider clock,
        FileTransferIngressFilter ingressFilter,
        IBlobStorageService blobStorage,
        INetworkEndpointDiscoveryPort endpointDiscovery,
        ITransferSessionRuntime runtime,
        ITransferSessionCertificateStore certificateStore,
        ITransferSessionTransportStore transportStore)
    {
        _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _payloadSender = payloadSender ?? throw new ArgumentNullException(nameof(payloadSender));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _trackerPort = trackerPort ?? throw new ArgumentNullException(nameof(trackerPort));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ingressFilter = ingressFilter ?? throw new ArgumentNullException(nameof(ingressFilter));
        _blobStorage = blobStorage ?? throw new ArgumentNullException(nameof(blobStorage));
        _endpointDiscovery = endpointDiscovery ?? throw new ArgumentNullException(nameof(endpointDiscovery));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
        _transportStore = transportStore ?? throw new ArgumentNullException(nameof(transportStore));
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
        if (!_ingressFilter.IsEnabled)
        {
            return DomainResult<TransferSessionId>.Failure(
                new DomainError(FileTransferIngressFilter.PolicyDisabledErrorCode, FileTransferIngressFilter.PrivacyNotice));
        }

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

        SwarmRendezvousId rendezvousId = SwarmRendezvousId.Generate();
        EphemeralSwarmPeerId ephemeralPeerId = EphemeralSwarmPeerId.Generate();
        StreamAuthToken streamAuthToken = StreamAuthToken.Generate();
        SeedRatio enforcedSeedRatio = targetSeedRatio ?? SeedRatio.Unlimited;
        bool isGroupTransfer = recipients.Count > 1;

        using TransferSessionCertificateMaterial certMaterial = await _certificateStore.GetOrCreateAsync(sessionId, ct);

        TransferSession session = isGroupTransfer
            ? TransferSession.CreateOutgoingMultiPeer(sessionId, localInitiator, recipients, manifest, enforcedSeedRatio, _clock)
            : TransferSession.CreateOutgoing1to1(sessionId, localInitiator, recipients[0], manifest, enforcedSeedRatio, _clock);

        IReadOnlyList<DirectEndpointCandidate> rawCandidates = await _endpointDiscovery.DiscoverLocalCandidatesAsync(wanPolicy, ct);
        IReadOnlyList<DirectEndpointCandidate> candidates = PexRateLimiter.FilterAndDeduplicate(rawCandidates, wanPolicy);

        RelayRendezvousDescriptor? relayRendezvous = await _endpointDiscovery.AllocateRelayRendezvousAsync(sessionId, ct);

        // Announce as Seeder to the blinded Exchange swarm tracker (zero Persona IDs, zero IP candidates)
        SwarmAnnounceResult announceResult = await _trackerPort.AnnounceAsync(rendezvousId, ephemeralPeerId, SwarmPeerRole.Seeder, ct: ct);

        var transportContext = new TransferSessionTransportContext(
            sessionId,
            channelId,
            localInitiator,
            rendezvousId,
            ephemeralPeerId,
            streamAuthToken,
            wanPolicy,
            relayRendezvous,
            candidates,
            sourceRoot,
            IsGroupTransfer: isGroupTransfer,
            SenderCertSha256: certMaterial.Fingerprint);

        await _sessionRepository.SaveSessionAsync(session, ct);
        await _transportStore.SaveTransportContextAsync(transportContext, ct);
        await _runtime.StartOrUpdateSessionAsync(
            session,
            transportContext,
            sourceRoot,
            announceResult.StunEndpoint,
            announceResult.SupportsTransferPipe,
            announceResult.ExchangeEndpoint,
            ct);

        ReadOnlyMemory<byte> serializedManifest = _serializer.Serialize(manifest);
        ManifestDistribution manifestDist;
        if (serializedManifest.Length <= IngressTransferOfferValidator.MaxInlineManifestBytes)
        {
            manifestDist = ManifestDistribution.FromInline(InlineManifestData.FromSpan(serializedManifest.Span));
        }
        else
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
            if (string.IsNullOrWhiteSpace(blobRef.RelayEndpointUri))
                return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.MissingRelayEndpoint", "Manifest blob upload did not return a relay endpoint URI."));

            manifestDist = ManifestDistribution.FromBlob(new ManifestBlobDescriptor(
                blobRef.Id,
                ChunkHash.FromBytes(blobRef.CiphertextSha256),
                TotalBundleBytes.FromBytes(serializedManifest.Length),
                RelayPipeEndpointUri.Parse(blobRef.RelayEndpointUri),
                blobRef.EncryptionKey,
                blobRef.BaseNonce));
        }

        FileTransferOfferPayload offerPayload = new(
            sessionId,
            TransferBundleSummary.Create(bundleTitle, manifest.TotalSize, manifest.TotalFiles, chunkSize, manifest.TotalChunks, merkleTree.Root),
            manifestDist,
            rendezvousId,
            streamAuthToken,
            relayRendezvous,
            candidates,
            certMaterial.Fingerprint,
            isGroupTransfer);

        byte[] inBandBytes = PrependOpcode(FileTransferControlOpcode.Offer, _serializer.Serialize(offerPayload).Span);

        if (isGroupTransfer)
        {
            // Group V2 SenderKey broadcast: RecipientIdentityId MUST be null + DeliveryRoute.RelayedGroup
            var groupOutbound = new OutboundPayloadContext(
                channelId,
                localInitiator,
                RecipientIdentityId: null,
                DeviceId.Primary,
                AppId.FileTransferControl,
                inBandBytes,
                DeliveryRoute.RelayedGroup);

            await _payloadSender.SendPayloadAsync(groupOutbound, ct);
        }
        else
        {
            var outboundContext = new OutboundPayloadContext(
                channelId,
                localInitiator,
                recipients[0],
                DeviceId.Primary,
                AppId.FileTransferControl,
                inBandBytes,
                DeliveryRoute.RelayedOneToOne);

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
        if (!_ingressFilter.IsEnabled)
        {
            return DomainResult.Failure(
                new DomainError(FileTransferIngressFilter.PolicyDisabledErrorCode, FileTransferIngressFilter.PrivacyNotice));
        }

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

        // Scan existing partial files on disk to compute resumption bitfield
        Bitfield resumptionBitfield = await _fileStorage.ScanAndComputeBitfieldAsync(
            destinationFolder,
            session.Manifest,
            chosenFiles,
            ct);

        DomainResult acceptResult = session.AcceptTransfer(chosenFiles, _clock, resumptionBitfield);
        if (acceptResult.IsFailure)
            return acceptResult;

        session.RegisterSwarmMember(
            session.InitiatorId,
            Bitfield.Full(session.Manifest.TotalChunks),
            _clock,
            peerChokingUs: PeerChokeStatus.Unchoked,
            weChokingPeer: PeerChokeStatus.Unchoked);

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

        using TransferSessionCertificateMaterial certMaterial = await _certificateStore.GetOrCreateAsync(sessionId, ct);

        TransferSessionTransportContext? transportContext = await _transportStore.GetTransportContextAsync(sessionId, ct);
        if (transportContext == null)
        {
            return DomainResult.Failure(new DomainError(
                "Transfer.TransportContextNotFound",
                $"Transport context for transfer session '{sessionId}' was not found."));
        }

        DirectWanPolicy wanPolicy = transportContext.WanPolicy;

        IReadOnlyList<DirectEndpointCandidate> rawReceiverCandidates = await _endpointDiscovery.DiscoverLocalCandidatesAsync(wanPolicy, ct);
        IReadOnlyList<DirectEndpointCandidate> receiverCandidates = PexRateLimiter.FilterAndDeduplicate(rawReceiverCandidates, wanPolicy);

        ChannelId channelId = transportContext.ChannelId;
        PublicIdentityId localSenderId = transportContext.LocalIdentityId;

        TransferSessionTransportContext updatedContext = transportContext with { LocalRootPath = destinationFolder };
        await _transportStore.SaveTransportContextAsync(updatedContext, ct);

        // Announce to blinded Exchange swarm tracker (zero Persona IDs, zero IP candidates)
        SwarmPeerRole role = session.IsDownloadComplete ? SwarmPeerRole.Seeder : SwarmPeerRole.Leecher;
        SwarmAnnounceResult announceResult = await _trackerPort.AnnounceAsync(
            updatedContext.SwarmRendezvousId,
            updatedContext.EphemeralPeerId,
            role,
            ct: ct);

        await _runtime.StartOrUpdateSessionAsync(
            session,
            updatedContext,
            destinationFolder,
            announceResult.StunEndpoint,
            announceResult.SupportsTransferPipe,
            announceResult.ExchangeEndpoint,
            ct);

        if (updatedContext.SenderCertSha256 != null)
        {
            await _runtime.RegisterPinnedPeerCertificateAsync(
                sessionId,
                session.InitiatorId,
                updatedContext.SenderCertSha256,
                ct: ct);
        }

        TransportRoutingPreference routingPref = (transportContext.DirectCandidates.Count > 0 || receiverCandidates.Count > 0)
            ? TransportRoutingPreference.DirectPreferred
            : TransportRoutingPreference.RelayOnly;

        if (transportContext is { IsGroupTransfer: true })
        {
            // Group V2 SenderKey broadcast: all group members pin our cert fingerprint and swarm directly
            FileTransferPeerJoinPayload peerJoinPayload = new(
                sessionId,
                transportContext.EphemeralPeerId,
                certMaterial.Fingerprint);

            byte[] peerJoinBytes = PrependOpcode(
                FileTransferControlOpcode.PeerJoin,
                _serializer.Serialize(peerJoinPayload).Span);

            var groupJoinContext = new OutboundPayloadContext(
                channelId,
                localSenderId,
                RecipientIdentityId: null,
                DeviceId.Primary,
                AppId.FileTransferControl,
                peerJoinBytes,
                DeliveryRoute.RelayedGroup);

            await _payloadSender.SendPayloadAsync(groupJoinContext, ct);
        }
        else
        {
            // Send in-band 1:1 accept payload over Double Ratchet from local recipient back to session.InitiatorId
            FileTransferAcceptPayload acceptPayload = new(
                sessionId,
                receiverCandidates,
                routingPref,
                session.LocalBitfield,
                certMaterial.Fingerprint);

            byte[] inBandBytes = PrependOpcode(FileTransferControlOpcode.Accept, _serializer.Serialize(acceptPayload).Span);

            var outboundContext = new OutboundPayloadContext(
                channelId,
                localSenderId,
                session.InitiatorId,
                DeviceId.Primary,
                AppId.FileTransferControl,
                inBandBytes,
                DeliveryRoute.RelayedOneToOne);

            await _payloadSender.SendPayloadAsync(outboundContext, ct);
        }

        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> PauseTransferAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = _runtime.GetActiveSession(sessionId)
            ?? await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Pause(_clock);
        if (result.IsSuccess)
        {
            await _sessionRepository.SaveSessionAsync(session, ct);
            bool wasActiveInRuntime = _runtime.HasActiveSession(sessionId);
            await _runtime.StopSessionAsync(sessionId, deleteCertificate: false, ct);

            if (!wasActiveInRuntime)
            {
                TransferSessionTransportContext? transportContext = await _transportStore.GetTransportContextAsync(sessionId, ct);
                if (transportContext != null)
                {
                    await _trackerPort.UnregisterAsync(transportContext.SwarmRendezvousId, transportContext.EphemeralPeerId, ct: ct);
                }
            }
        }
        return result;
    }

    public async ValueTask<DomainResult> ResumeTransferAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = _runtime.GetActiveSession(sessionId)
            ?? await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        bool resumingFromVaultLock = !_runtime.HasActiveSession(sessionId) &&
            session.State is TransferSessionState.InFlight or TransferSessionState.Seeding or TransferSessionState.Stalled;

        DomainResult result = resumingFromVaultLock
            ? DomainResult.Success()
            : session.Resume(_clock);

        if (result.IsSuccess)
        {
            TransferSessionTransportContext? transportContext = await _transportStore.GetTransportContextAsync(sessionId, ct);
            if (transportContext?.LocalRootPath != null &&
                session.Role == TransferRole.Recipient &&
                !session.IsDownloadComplete)
            {
                SelectedFileIndices chosenFiles = session.SelectedFiles ?? SelectedFileIndices.All(session.Manifest.TotalFiles);
                Bitfield scannedBitfield = await _fileStorage.ScanAndComputeBitfieldAsync(
                    transportContext.LocalRootPath,
                    session.Manifest,
                    chosenFiles,
                    ct);

                session.ApplyResumptionBitfield(scannedBitfield, _clock);
            }

            await _sessionRepository.SaveSessionAsync(session, ct);

            if (transportContext != null)
            {
                SwarmPeerRole role = session.Role == TransferRole.Sender || session.IsDownloadComplete
                    ? SwarmPeerRole.Seeder
                    : SwarmPeerRole.Leecher;

                SwarmAnnounceResult announceResult = await _trackerPort.AnnounceAsync(
                    transportContext.SwarmRendezvousId,
                    transportContext.EphemeralPeerId,
                    role,
                    ct: ct);

                if (transportContext.LocalRootPath != null)
                {
                    await _runtime.StartOrUpdateSessionAsync(
                        session,
                        transportContext,
                        transportContext.LocalRootPath,
                        announceResult.StunEndpoint,
                        announceResult.SupportsTransferPipe,
                        announceResult.ExchangeEndpoint,
                        ct);

                    if (transportContext.SenderCertSha256 != null &&
                        session.InitiatorId != transportContext.LocalIdentityId)
                    {
                        await _runtime.RegisterPinnedPeerCertificateAsync(
                            sessionId,
                            session.InitiatorId,
                            transportContext.SenderCertSha256,
                            ct: ct);
                    }
                }

                // Re-announce resumption bitfield + vault-persisted session cert to reconnect after Pause or Vault Lock
                if (session.Role == TransferRole.Recipient && !session.IsDownloadComplete)
                {
                    using TransferSessionCertificateMaterial certMaterial = await _certificateStore.GetOrCreateAsync(sessionId, ct);
                    if (transportContext.IsGroupTransfer)
                    {
                        FileTransferPeerJoinPayload peerJoinPayload = new(
                            sessionId,
                            transportContext.EphemeralPeerId,
                            certMaterial.Fingerprint);

                        byte[] peerJoinBytes = PrependOpcode(
                            FileTransferControlOpcode.PeerJoin,
                            _serializer.Serialize(peerJoinPayload).Span);

                        await _payloadSender.SendPayloadAsync(
                            new OutboundPayloadContext(
                                transportContext.ChannelId,
                                transportContext.LocalIdentityId,
                                RecipientIdentityId: null,
                                DeviceId.Primary,
                                AppId.FileTransferControl,
                                peerJoinBytes,
                                DeliveryRoute.RelayedGroup),
                            ct);
                    }
                    else
                    {
                        FileTransferAcceptPayload resumeAccept = new(
                            sessionId,
                            [],
                            TransportRoutingPreference.DirectPreferred,
                            session.LocalBitfield,
                            certMaterial.Fingerprint);

                        byte[] acceptBytes = PrependOpcode(
                            FileTransferControlOpcode.Accept,
                            _serializer.Serialize(resumeAccept).Span);

                        await _payloadSender.SendPayloadAsync(
                            new OutboundPayloadContext(
                                transportContext.ChannelId,
                                transportContext.LocalIdentityId,
                                session.InitiatorId,
                                DeviceId.Primary,
                                AppId.FileTransferControl,
                                acceptBytes,
                                DeliveryRoute.RelayedOneToOne),
                            ct);
                    }
                }
            }
        }
        return result;
    }

    public async ValueTask<DomainResult> StopSeedingAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = _runtime.GetActiveSession(sessionId)
            ?? await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.StopSeeding(_clock);
        if (result.IsSuccess)
        {
            await _sessionRepository.SaveSessionAsync(session, ct);
            bool wasActiveInRuntime = _runtime.HasActiveSession(sessionId);
            await _runtime.StopSessionAsync(sessionId, deleteCertificate: true, ct);

            if (!wasActiveInRuntime)
            {
                TransferSessionTransportContext? transportContext = await _transportStore.GetTransportContextAsync(sessionId, ct);
                if (transportContext != null)
                {
                    await _trackerPort.UnregisterAsync(transportContext.SwarmRendezvousId, transportContext.EphemeralPeerId, ct: ct);
                }
            }
        }

        return result;
    }

    public async ValueTask<DomainResult> CancelTransferAsync(TransferSessionId sessionId, TransferTerminationReason reason, CancellationToken ct = default)
    {
        TransferSession? session = _runtime.GetActiveSession(sessionId)
            ?? await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Cancel(reason, _clock);
        if (result.IsSuccess)
        {
            await _sessionRepository.SaveSessionAsync(session, ct);

            TransferSessionTransportContext? transportContext = await _transportStore.GetTransportContextAsync(sessionId, ct);
            PublicIdentityId localIdentity = transportContext?.LocalIdentityId ?? session.InitiatorId;
            ChannelId channelId = transportContext?.ChannelId ?? ChannelId.FromGuid(sessionId.Value);

            bool wasActiveInRuntime = _runtime.HasActiveSession(sessionId);
            await _runtime.StopSessionAsync(sessionId, deleteCertificate: true, ct);

            if (!wasActiveInRuntime && transportContext != null)
            {
                await _trackerPort.UnregisterAsync(transportContext.SwarmRendezvousId, transportContext.EphemeralPeerId, ct: ct);
            }

            // Send in-band cancel payload
            FileTransferCancelPayload cancelPayload = new(sessionId, reason);
            byte[] inBandBytes = PrependOpcode(FileTransferControlOpcode.Cancel, _serializer.Serialize(cancelPayload).Span);

            if (transportContext is { IsGroupTransfer: true })
            {
                var groupCancel = new OutboundPayloadContext(
                    channelId,
                    localIdentity,
                    RecipientIdentityId: null,
                    DeviceId.Primary,
                    AppId.FileTransferControl,
                    inBandBytes,
                    DeliveryRoute.RelayedGroup);

                await _payloadSender.SendPayloadAsync(groupCancel, ct);
            }
            else
            {
                PublicIdentityId targetRecipient = DetermineCancelTarget(session, localIdentity);
                var outboundContext = new OutboundPayloadContext(
                    channelId,
                    localIdentity,
                    targetRecipient,
                    DeviceId.Primary,
                    AppId.FileTransferControl,
                    inBandBytes,
                    DeliveryRoute.RelayedOneToOne);

                await _payloadSender.SendPayloadAsync(outboundContext, ct);
            }
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
            TransferTopology.MultiPeer multiPeer when multiPeer.Recipients.Count > 0 => multiPeer.Recipients[0],
            _ => session.InitiatorId
        };
    }

    private static byte[] PrependOpcode(FileTransferControlOpcode opcode, ReadOnlySpan<byte> payload)
    {
        byte[] framed = new byte[1 + payload.Length];
        framed[0] = (byte)opcode;
        payload.CopyTo(framed.AsSpan(1));
        return framed;
    }
}
