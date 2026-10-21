using System.Buffers;
using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Features.MultiStreamEngine;

public sealed class TransferSessionCoordinator
{
    private readonly ChunkPipelineScheduler _scheduler;
    private readonly DiskChunkAssembler _assembler;
    private readonly IDateTimeProvider _clock;
    private readonly ITransferRateLimiter? _rateLimiter;
    private readonly object _sessionSync = new();
    private readonly Dictionary<PublicIdentityId, int> _peerOffenses = [];

    public ChunkPipelineScheduler Scheduler => _scheduler;

    public TransferSessionCoordinator(
        ChunkPipelineScheduler scheduler,
        DiskChunkAssembler assembler,
        IDateTimeProvider clock,
        ITransferRateLimiter? rateLimiter = null)
    {
        _scheduler = scheduler;
        _assembler = assembler;
        _clock = clock;
        _rateLimiter = rateLimiter;
    }

    public int GetPeerOffenses(PublicIdentityId peerId)
    {
        lock (_sessionSync)
        {
            return _peerOffenses.TryGetValue(peerId, out int count) ? count : 0;
        }
    }

    public async ValueTask<bool> AuthenticateHandshakeAsync(
        HandshakeFrame handshake,
        StreamAuthToken expectedAuthToken,
        TransferSessionId expectedSessionId,
        IReadOnlyCollection<PublicIdentityId> authorizedPeers,
        ITransferStream stream,
        CancellationToken ct = default)
    {
        if (handshake.SessionId != expectedSessionId ||
            !CryptographicOperations.FixedTimeEquals(handshake.AuthToken.Span, expectedAuthToken.Span) ||
            !authorizedPeers.Contains(handshake.SenderPeerId))
        {
            await stream.DisposeAsync();
            return false;
        }

        return true;
    }

    public async ValueTask<DomainResult> HandleRequestChunkAsync(
        TransferSession session,
        PublicIdentityId requesterId,
        RequestChunkFrame request,
        ITransferStream stream,
        IDesktopFileStorage fileStorage,
        RootDirectoryPath rootPath,
        TransferKey key,
        BaseNonce baseNonce,
        CancellationToken ct = default)
    {
        if (!_scheduler.TryRecordInboundRequest(requesterId))
        {
            lock (_sessionSync)
            {
                session.SetWeChokingPeer(requesterId, PeerChokeStatus.Choked);
            }
            return DomainResult.Failure(new DomainError("MultiStream.QueueFull", $"Peer '{requesterId}' exceeded in-flight request limit."));
        }

        byte[]? rentedPlaintext = null;
        try
        {
            ChunkSizeBytes chunkSize = session.Manifest.ChunkSize;
            ChunkIndex chunkIdx = request.ChunkIndex;
            long startOffset = (long)chunkIdx.Value * chunkSize.Bytes;
            long remainingBytes = session.Manifest.TotalSize.Bytes - startOffset;
            int length = (int)Math.Min((long)chunkSize.Bytes, Math.Max(0, remainingBytes));
            ChunkByteLength actualLength = ChunkByteLength.FromBytes(length);

            if (_rateLimiter != null)
            {
                await _rateLimiter.ThrottleUploadAsync(session.Id, TransferByteLength.FromBytes(length), ct);
            }

            IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(session.Manifest, chunkIdx, actualLength);
            rentedPlaintext = ArrayPool<byte>.Shared.Rent(length);

            foreach (FileChunkSlice slice in slices)
            {
                FileEntry entry = session.Manifest.Files[slice.FileIndex.Value];
                ResolvedFilePath filePath = ResolvedFilePath.Resolve(rootPath, entry.RelativePath);
                PlaintextChunkData sliceData = await fileStorage.ReadChunkAsync(filePath, slice.OffsetInFile, slice.SliceLength, ct);
                sliceData.Span.CopyTo(rentedPlaintext.AsSpan(slice.BufferOffset, slice.SliceLength.Bytes));
            }

            byte[] encryptedBuffer = new byte[length + ChunkCryptoService.TagSizeBytes];
            ChunkCryptoService.EncryptChunkInto(
                rentedPlaintext.AsSpan(0, length),
                key,
                baseNonce,
                chunkIdx,
                encryptedBuffer);

            EncryptedChunkData encrypted = EncryptedChunkData.FromMemory(encryptedBuffer);
            await stream.SendFrameAsync(new ChunkDataFrame(chunkIdx, encrypted), ct);

            lock (_sessionSync)
            {
                session.RecordUploadedBytes(TransferByteLength.FromBytes(length), _clock);
            }
            return DomainResult.Success();
        }
        finally
        {
            if (rentedPlaintext != null)
            {
                ArrayPool<byte>.Shared.Return(rentedPlaintext);
            }
            _scheduler.DecrementInboundRequest(requesterId);
        }
    }

    public async ValueTask<DomainResult> HandleIncomingChunkDataAsync(
        TransferSession session,
        PublicIdentityId senderId,
        ChunkDataFrame chunkFrame,
        ITransferStream stream,
        TransferKey key,
        BaseNonce baseNonce,
        RootDirectoryPath destinationFolder,
        CancellationToken ct = default)
    {
        // 1. Unsolicited check: if chunk was not scheduled/in-flight, drop immediately without disk write
        if (!_scheduler.IsChunkInFlight(chunkFrame.ChunkIndex))
        {
            return DomainResult.Failure(new DomainError("MultiStream.UnsolicitedChunk", $"Received unsolicited chunk index {chunkFrame.ChunkIndex.Value}."));
        }

        if (chunkFrame.Data.Length < ChunkCryptoService.TagSizeBytes)
        {
            await HandlePeerCryptoOrHashFailureAsync(session, senderId, chunkFrame.ChunkIndex, stream);
            return DomainResult.Failure(new DomainError("MultiStream.CryptoAuthFailed", "Chunk AES-GCM authentication failed."));
        }

        int plaintextLength = chunkFrame.Data.Length - ChunkCryptoService.TagSizeBytes;
        if (_rateLimiter != null)
        {
            await _rateLimiter.ThrottleDownloadAsync(session.Id, TransferByteLength.FromBytes(plaintextLength), ct);
        }

        byte[] rentedPlaintext = ArrayPool<byte>.Shared.Rent(Math.Max(1, plaintextLength));
        try
        {
            try
            {
                ChunkCryptoService.DecryptChunkInto(
                    chunkFrame.Data.Span,
                    key,
                    baseNonce,
                    chunkFrame.ChunkIndex,
                    rentedPlaintext.AsSpan(0, plaintextLength));
            }
            catch (CryptographicException)
            {
                await HandlePeerCryptoOrHashFailureAsync(session, senderId, chunkFrame.ChunkIndex, stream);
                return DomainResult.Failure(new DomainError("MultiStream.CryptoAuthFailed", "Chunk AES-GCM authentication failed."));
            }

            PlaintextChunkData plaintext = PlaintextChunkData.FromMemory(rentedPlaintext.AsMemory(0, plaintextLength));

            // 2. Verify Merkle leaf hash ONCE inside session.VerifyAndRecordChunk (via DiskChunkAssembler) BEFORE writing to disk
            DomainResult assembleResult = await _assembler.AssembleChunkAsync(
                session,
                destinationFolder,
                chunkFrame.ChunkIndex,
                plaintext,
                _clock,
                ct);

            if (assembleResult.IsSuccess)
            {
                _scheduler.MarkCompleted(chunkFrame.ChunkIndex);
                return assembleResult;
            }

            if (assembleResult.Error.Code == "Transfer.CryptographicVerificationFailed")
            {
                await HandlePeerCryptoOrHashFailureAsync(session, senderId, chunkFrame.ChunkIndex, stream);
                return DomainResult.Failure(new DomainError("MultiStream.CorruptedChunk", "Merkle leaf hash mismatch."));
            }

            _scheduler.ReleaseChunk(chunkFrame.ChunkIndex);
            return assembleResult;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedPlaintext);
        }
    }

    public async ValueTask<DomainResult> PumpPeerStreamsAsync(
        TransferSession session,
        PublicIdentityId peerId,
        ITransferStreamPool streamPool,
        TransferKey key,
        BaseNonce baseNonce,
        RootDirectoryPath destinationFolder,
        CancellationToken ct = default)
    {
        int streamCount = streamPool.ActiveStreams.Count;
        Task<DomainResult>[] workerTasks = new Task<DomainResult>[streamCount];

        for (int i = 0; i < streamCount; i++)
        {
            ITransferStream stream = streamPool.GetStream(StreamIndex.FromInt(i));
            workerTasks[i] = PumpSingleStreamAsync(
                session,
                peerId,
                stream,
                key,
                baseNonce,
                destinationFolder,
                ct);
        }

        DomainResult[] results = await Task.WhenAll(workerTasks);
        foreach (DomainResult result in results)
        {
            if (result.IsFailure)
            {
                return result;
            }
        }

        return DomainResult.Success();
    }

    private async Task<DomainResult> PumpSingleStreamAsync(
        TransferSession session,
        PublicIdentityId peerId,
        ITransferStream stream,
        TransferKey key,
        BaseNonce baseNonce,
        RootDirectoryPath destinationFolder,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool isComplete;
            lock (_sessionSync)
            {
                isComplete = session.IsDownloadComplete ||
                             session.State == TransferSessionState.Completed ||
                             session.State == TransferSessionState.Seeding ||
                             session.State == TransferSessionState.Cancelled ||
                             session.State == TransferSessionState.Failed;
            }

            if (isComplete)
            {
                return DomainResult.Success();
            }

            // Fill pipeline window on this stream
            bool scheduledAny = false;
            while (_scheduler.CanRequest(peerId))
            {
                ChunkIndex? nextChunk;
                lock (_sessionSync)
                {
                    Bitfield required = session.SelectedFiles != null
                        ? session.Manifest.GetRequiredChunks(session.SelectedFiles)
                        : Bitfield.Full(session.Manifest.TotalChunks);

                    nextChunk = RarestFirstChunkSelector.SelectNextChunk(
                        session.LocalBitfield,
                        required,
                        session.SwarmMembers,
                        _scheduler.InFlightChunks);

                    if (nextChunk.HasValue && !_scheduler.TrySchedule(peerId, nextChunk.Value))
                    {
                        nextChunk = null;
                    }
                }

                if (!nextChunk.HasValue)
                {
                    break;
                }

                await stream.SendFrameAsync(new RequestChunkFrame(nextChunk.Value), ct);
                scheduledAny = true;
            }

            if (!scheduledAny && _scheduler.GetPendingCount(peerId) == 0)
            {
                return DomainResult.Success();
            }

            IStreamFramePayload frame = await stream.ReceiveFrameAsync(ct);
            if (frame is ChunkDataFrame chunkData)
            {
                DomainResult result = await HandleIncomingChunkDataAsync(
                    session,
                    peerId,
                    chunkData,
                    stream,
                    key,
                    baseNonce,
                    destinationFolder,
                    ct);

                if (result.IsFailure)
                {
                    return result;
                }
            }
            else if (frame is ChokeFrame)
            {
                lock (_sessionSync)
                {
                    session.RecordPeerChokingUs(peerId, PeerChokeStatus.Choked);
                }
                _scheduler.ReleasePeer(peerId);
                return DomainResult.Success();
            }
        }

        return DomainResult.Success();
    }

    private async ValueTask HandlePeerCryptoOrHashFailureAsync(
        TransferSession session,
        PublicIdentityId senderId,
        ChunkIndex chunkIndex,
        ITransferStream stream)
    {
        lock (_sessionSync)
        {
            session.SetWeChokingPeer(senderId, PeerChokeStatus.Choked);
            int current = _peerOffenses.TryGetValue(senderId, out int count) ? count : 0;
            _peerOffenses[senderId] = current + 1;
        }
        _scheduler.ReleaseChunk(chunkIndex);
        await stream.DisposeAsync();
    }
}
