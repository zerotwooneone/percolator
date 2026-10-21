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
    private readonly Dictionary<PublicIdentityId, int> _peerOffenses = [];

    public ChunkPipelineScheduler Scheduler => _scheduler;

    public TransferSessionCoordinator(
        ChunkPipelineScheduler scheduler,
        DiskChunkAssembler assembler,
        IDateTimeProvider clock)
    {
        _scheduler = scheduler;
        _assembler = assembler;
        _clock = clock;
    }

    public int GetPeerOffenses(PublicIdentityId peerId) =>
        _peerOffenses.TryGetValue(peerId, out int count) ? count : 0;

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
            // Bounded queue exceeded: choke peer and drop request
            session.SetWeChokingPeer(requesterId, PeerChokeStatus.Choked);
            return DomainResult.Failure(new DomainError("MultiStream.QueueFull", $"Peer '{requesterId}' exceeded in-flight request limit."));
        }

        try
        {
            // Read chunk slices across files and assemble plaintext
            ChunkSizeBytes chunkSize = session.Manifest.ChunkSize;
            ChunkIndex chunkIdx = request.ChunkIndex;
            long startOffset = (long)chunkIdx.Value * chunkSize.Bytes;
            long remainingBytes = session.Manifest.TotalSize.Bytes - startOffset;
            int length = (int)Math.Min((long)chunkSize.Bytes, Math.Max(0, remainingBytes));
            ChunkByteLength actualLength = ChunkByteLength.FromBytes(length);

            IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(session.Manifest, chunkIdx, actualLength);
            byte[] chunkBuffer = new byte[length];
            foreach (FileChunkSlice slice in slices)
            {
                FileEntry entry = session.Manifest.Files[slice.FileIndex.Value];
                ResolvedFilePath filePath = ResolvedFilePath.Resolve(rootPath, entry.RelativePath);
                PlaintextChunkData sliceData = await fileStorage.ReadChunkAsync(filePath, slice.OffsetInFile, slice.SliceLength, ct);
                sliceData.Span.CopyTo(chunkBuffer.AsSpan(slice.BufferOffset, slice.SliceLength.Bytes));
            }

            PlaintextChunkData plaintext = PlaintextChunkData.FromBytes(chunkBuffer);
            EncryptedChunkData encrypted = ChunkCryptoService.EncryptChunk(plaintext, key, baseNonce, chunkIdx);

            await stream.SendFrameAsync(new ChunkDataFrame(chunkIdx, encrypted), ct);
            session.RecordUploadedBytes(TransferByteLength.FromBytes(length), _clock);
            return DomainResult.Success();
        }
        finally
        {
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

        PlaintextChunkData plaintext;
        try
        {
            plaintext = ChunkCryptoService.DecryptChunk(chunkFrame.Data, key, baseNonce, chunkFrame.ChunkIndex);
        }
        catch (CryptographicException)
        {
            // Cryptographic authentication failed: choke peer, record offense, terminate stream, reschedule chunk
            session.SetWeChokingPeer(senderId, PeerChokeStatus.Choked);
            _peerOffenses[senderId] = GetPeerOffenses(senderId) + 1;
            _scheduler.ReleaseChunk(chunkFrame.ChunkIndex);
            await stream.DisposeAsync();
            return DomainResult.Failure(new DomainError("MultiStream.CryptoAuthFailed", "Chunk AES-GCM authentication failed."));
        }

        // 2. Merkle leaf hash check
        ChunkHash expectedHash = session.Manifest.MerkleTree.GetLeaf(chunkFrame.ChunkIndex);
        ChunkHash actualHash = MerkleTreeBuilder.HashLeaf(plaintext.Span);
        if (actualHash != expectedHash)
        {
            // Corrupted chunk verification: choke peer, increment offense, terminate stream, reschedule chunk
            session.SetWeChokingPeer(senderId, PeerChokeStatus.Choked);
            _peerOffenses[senderId] = GetPeerOffenses(senderId) + 1;
            _scheduler.ReleaseChunk(chunkFrame.ChunkIndex);
            await stream.DisposeAsync();
            return DomainResult.Failure(new DomainError("MultiStream.CorruptedChunk", "Merkle leaf hash mismatch."));
        }

        // 3. Assemble chunk to disk
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
        }
        else
        {
            _scheduler.ReleaseChunk(chunkFrame.ChunkIndex);
        }

        return assembleResult;
    }
}
