using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
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
    private readonly IDistributedTrackerPort _trackerPort;
    private readonly IDateTimeProvider _clock;

    public FileTransferActionHandler(
        ITransferSessionRepository sessionRepository,
        IDesktopFileStorage fileStorage,
        IPayloadSender payloadSender,
        IDistributedTrackerPort trackerPort,
        IDateTimeProvider clock)
    {
        _sessionRepository = sessionRepository;
        _fileStorage = fileStorage;
        _payloadSender = payloadSender;
        _trackerPort = trackerPort;
        _clock = clock;
    }

    public async ValueTask<DomainResult<TransferSessionId>> InitiateTransferAsync(
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

        // Scan files and build contiguous FileEntries
        List<FileEntry> entries = [];
        long cumulativeOffset = 0;
        for (int i = 0; i < filePaths.Count; i++)
        {
            SanitizedRelativePath relPath = filePaths[i];
            ResolvedFilePath absPath = ResolvedFilePath.Resolve(sourceRoot, relPath);
            FileInfo info = new(absPath.AbsolutePath);
            if (!info.Exists)
                return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.FileNotFound", $"File not found: '{relPath.Value}'."));

            FileSizeBytes size = FileSizeBytes.FromBytes(info.Length);
            BundleByteOffset startOffset = BundleByteOffset.FromOffset(cumulativeOffset);
            FileLastModifiedUtc lastModified = FileLastModifiedUtc.FromDateTimeOffset(info.LastWriteTimeUtc);

            entries.Add(new FileEntry(FileIndex.FromInt(i), relPath, size, startOffset, lastModified));
            cumulativeOffset += size.Bytes;
        }

        if (cumulativeOffset > IngressTransferOfferValidator.MaxBundleSizeBytes)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.SizeLimitExceeded", "Bundle total size exceeds 16 TB."));

        ChunkSizeBytes chunkSize = ChunkSizeBytes.Default;
        int chunkCount = (int)Math.Max(1, (cumulativeOffset + chunkSize.Bytes - 1) / chunkSize.Bytes);
        if (chunkCount > IngressTransferOfferValidator.MaxChunksPerBundle)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.ChunkLimitExceeded", "Bundle total chunk count exceeds 1,000,000."));

        // Build leaf hashes and Merkle tree
        List<ChunkHash> leafHashes = new(chunkCount);
        for (int c = 0; c < chunkCount; c++)
        {
            ChunkIndex chunkIdx = ChunkIndex.FromInt(c);
            ChunkByteLength expectedSize = ChunkByteLength.FromBytes((int)Math.Min((long)chunkSize.Bytes, cumulativeOffset - ((long)c * chunkSize.Bytes)));

            byte[] chunkBuffer = new byte[expectedSize.Bytes];
            IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlicesFromEntries(entries, chunkSize, chunkIdx, expectedSize);
            foreach (FileChunkSlice slice in slices)
            {
                FileEntry entry = entries[slice.FileIndex.Value];
                ResolvedFilePath filePath = ResolvedFilePath.Resolve(sourceRoot, entry.RelativePath);
                PlaintextChunkData sliceData = await _fileStorage.ReadChunkAsync(filePath, slice.OffsetInFile, slice.SliceLength, ct);
                sliceData.Span.CopyTo(chunkBuffer.AsSpan(slice.BufferOffset, slice.SliceLength.Bytes));
            }

            leafHashes.Add(MerkleTreeBuilder.HashLeaf(chunkBuffer));
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

        PublicIdentityId localInitiator = recipients[0]; // Bound to local identity
        TransferSession session = recipients.Count == 1
            ? TransferSession.CreateOutgoing1to1(sessionId, localInitiator, recipients[0], manifest, enforcedSeedRatio, _clock)
            : TransferSession.CreateOutgoingMultiPeer(sessionId, localInitiator, recipients, manifest, enforcedSeedRatio, _clock);

        await _sessionRepository.SaveSessionAsync(session, ct);

        List<DirectEndpointCandidate> candidates = [];
        if (wanPolicy == DirectWanPolicy.ExplicitlyAllowed)
        {
            // Direct WAN candidates when explicitly allowed
            candidates.Add(new DirectEndpointCandidate(EndpointHost.Parse("wan.example.com"), PortNumber.FromInt(50000), DirectCandidateKind.DirectWanUpnp));
        }

        byte[] serializedManifest = manifest.Serialize();
        ManifestDistribution manifestDist = serializedManifest.Length <= IngressTransferOfferValidator.MaxInlineManifestBytes
            ? ManifestDistribution.FromInline(InlineManifestData.FromBytes(serializedManifest))
            : ManifestDistribution.FromBlob(new ManifestBlobDescriptor(BlobId.FromSha256(SHA256.HashData(serializedManifest)), ChunkHash.FromBytes(SHA256.HashData(serializedManifest)), manifest.TotalSize, RelayPipeEndpointUri.Parse("https://relay.percolator.net/pipes")));

        FileTransferOfferPayload offerPayload = new(
            sessionId,
            TransferBundleSummary.Create(bundleTitle, manifest.TotalSize, manifest.TotalFiles, chunkSize, manifest.TotalChunks, merkleTree.Root),
            manifestDist,
            sessionKey,
            baseNonce,
            streamAuthToken,
            RelayRendezvous: null,
            candidates);

        byte[] inBandBytes = new byte[1 + offerPayload.Serialize().Length];
        inBandBytes[0] = (byte)FileTransferControlOpcode.Offer;
        offerPayload.Serialize().CopyTo(inBandBytes.AsSpan(1));

        foreach (PublicIdentityId recipient in recipients)
        {
            var outboundContext = new OutboundPayloadContext(
                ChannelId.FromGuid(sessionId.Value),
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

        DomainResult acceptResult = session.AcceptTransfer(chosenFiles, _clock);
        if (acceptResult.IsFailure)
            return acceptResult;

        // Immediate Finalization for Zero-Byte Files
        foreach (FileIndex idx in chosenFiles.Indices)
        {
            FileEntry file = session.Manifest.Files[idx.Value];
            if (file.FileSize.Bytes == 0)
            {
                ResolvedFilePath path = ResolvedFilePath.Resolve(destinationFolder, file.RelativePath);
                await _fileStorage.FinalizeFileAsync(path, file.LastModifiedUtc, ct);
            }
        }

        // Pre-allocate remaining non-empty files
        await _fileStorage.PreAllocateFilesAsync(destinationFolder, session.Manifest, chosenFiles, ct);
        await _sessionRepository.SaveSessionAsync(session, ct);

        // Send in-band accept payload
        FileTransferAcceptPayload acceptPayload = new(
            sessionId,
            ReceiverCandidates: [],
            TransportRoutingPreference.DirectPreferred,
            session.LocalBitfield);

        byte[] inBandBytes = new byte[1 + acceptPayload.Serialize().Length];
        inBandBytes[0] = (byte)FileTransferControlOpcode.Accept;
        acceptPayload.Serialize().CopyTo(inBandBytes.AsSpan(1));

        var outboundContext = new OutboundPayloadContext(
            ChannelId.FromGuid(sessionId.Value),
            session.InitiatorId,
            session.InitiatorId,
            DeviceId.Primary,
            AppId.FileTransferControl,
            inBandBytes,
            DeliveryRoute.DirectP2P);

        await _payloadSender.SendPayloadAsync(outboundContext, ct);

        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> PauseTransferAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Pause();
        if (result.IsSuccess) await _sessionRepository.SaveSessionAsync(session, ct);
        return result;
    }

    public async ValueTask<DomainResult> ResumeTransferAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Resume();
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
            // Proactive tracker unregistration
            SwarmRendezvousId rendezvous = SwarmRendezvousId.Derive(TransferKey.Generate());
            await _trackerPort.UnregisterAsync(rendezvous, session.InitiatorId, ct);

            // Send in-band cancel payload
            FileTransferCancelPayload cancelPayload = new(sessionId, reason);
            byte[] inBandBytes = new byte[1 + cancelPayload.Serialize().Length];
            inBandBytes[0] = (byte)FileTransferControlOpcode.Cancel;
            cancelPayload.Serialize().CopyTo(inBandBytes.AsSpan(1));

            var outboundContext = new OutboundPayloadContext(
                ChannelId.FromGuid(sessionId.Value),
                session.InitiatorId,
                session.InitiatorId,
                DeviceId.Primary,
                AppId.FileTransferControl,
                inBandBytes,
                DeliveryRoute.DirectP2P);

            await _payloadSender.SendPayloadAsync(outboundContext, ct);
        }
        return result;
    }
}
