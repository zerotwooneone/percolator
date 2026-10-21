using System.Collections.Concurrent;
using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Tests.TestDoubles;

public sealed class FakeDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; }

    public FakeDateTimeProvider(DateTimeOffset? initial = null)
    {
        UtcNow = initial ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public void Advance(TimeSpan span) => UtcNow = UtcNow.Add(span);
}

public sealed class InMemoryTransferSessionRepository : ITransferSessionRepository
{
    private readonly ConcurrentDictionary<TransferSessionId, TransferSession> _sessions = new();
    private readonly ConcurrentDictionary<TransferSessionId, TransferSessionTransportContext> _transportContexts = new();

    public ValueTask SaveSessionAsync(TransferSession session, CancellationToken ct = default)
    {
        _sessions[session.Id] = session;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TransferSession?> GetSessionByIdAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        _sessions.TryGetValue(sessionId, out var s);
        return ValueTask.FromResult<TransferSession?>(s);
    }

    public ValueTask<IReadOnlyList<TransferSession>> GetActiveSessionsAsync(CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<TransferSession>>(_sessions.Values.ToList());
    }

    public ValueTask SaveTransportContextAsync(TransferSessionTransportContext context, CancellationToken ct = default)
    {
        _transportContexts[context.SessionId] = context;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TransferSessionTransportContext?> GetTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        _transportContexts.TryGetValue(sessionId, out var ctx);
        return ValueTask.FromResult<TransferSessionTransportContext?>(ctx);
    }
}

public sealed class InMemoryDesktopFileStorage : IDesktopFileStorage
{
    private readonly object _sync = new();
    public DiskSpacePreflightResult DiskSpaceResult { get; set; } = DiskSpacePreflightResult.Sufficient();
    public TotalBundleBytes? LastCheckedBytes { get; private set; }
    public List<ResolvedFilePath> FinalizedFiles { get; } = [];
    public Dictionary<string, byte[]> FilesOnDisk { get; } = [];

    public ValueTask PreAllocateFilesAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices selectedIndices, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteChunkAsync(ResolvedFilePath targetPath, FileByteOffset fileOffset, PlaintextChunkData data, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!FilesOnDisk.TryGetValue(targetPath.AbsolutePath, out var buffer))
            {
                buffer = new byte[fileOffset.Offset + data.Length];
                FilesOnDisk[targetPath.AbsolutePath] = buffer;
            }
            else if (buffer.Length < fileOffset.Offset + data.Length)
            {
                byte[] expanded = new byte[fileOffset.Offset + data.Length];
                buffer.CopyTo(expanded, 0);
                FilesOnDisk[targetPath.AbsolutePath] = expanded;
                buffer = expanded;
            }

            data.Span.CopyTo(buffer.AsSpan((int)fileOffset.Offset, data.Length));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<PlaintextChunkData> ReadChunkAsync(ResolvedFilePath sourcePath, FileByteOffset fileOffset, TransferByteLength length, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!FilesOnDisk.TryGetValue(sourcePath.AbsolutePath, out var buffer))
            {
                buffer = new byte[(int)fileOffset.Offset + length.Bytes];
                FilesOnDisk[sourcePath.AbsolutePath] = buffer;
            }

            byte[] slice = new byte[length.Bytes];
            int copyLen = Math.Min(length.Bytes, Math.Max(0, buffer.Length - (int)fileOffset.Offset));
            if (copyLen > 0)
            {
                buffer.AsSpan((int)fileOffset.Offset, copyLen).CopyTo(slice);
            }

            return ValueTask.FromResult(PlaintextChunkData.FromBytes(slice));
        }
    }

    public ValueTask<FileMetadataResult> GetFileMetadataAsync(ResolvedFilePath sourcePath, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (FilesOnDisk.TryGetValue(sourcePath.AbsolutePath, out var buffer))
            {
                return ValueTask.FromResult(FileMetadataResult.Found(
                    FileSizeBytes.FromBytes(buffer.Length),
                    FileLastModifiedUtc.FromUnixSeconds(1_700_000_000)));
            }

            return ValueTask.FromResult(FileMetadataResult.Found(
                FileSizeBytes.FromBytes(1024),
                FileLastModifiedUtc.FromUnixSeconds(1_700_000_000)));
        }
    }

    public ValueTask<Bitfield> ScanAndComputeBitfieldAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices? selectedIndices = null, CancellationToken ct = default)
    {
        return ValueTask.FromResult(Bitfield.Empty(manifest.TotalChunks));
    }

    public ValueTask FinalizeFileAsync(ResolvedFilePath targetPath, FileLastModifiedUtc lastModifiedUtc, CancellationToken ct = default)
    {
        lock (_sync)
        {
            FinalizedFiles.Add(targetPath);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<DiskSpacePreflightResult> CheckDiskSpaceAsync(RootDirectoryPath rootPath, TotalBundleBytes requiredBytes, CancellationToken ct = default)
    {
        LastCheckedBytes = requiredBytes;
        return ValueTask.FromResult(DiskSpaceResult);
    }
}

public sealed class RecordingPayloadSender : IPayloadSender
{
    public List<OutboundPayloadContext> SentPayloads { get; } = [];

    public ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        SentPayloads.Add(context);
        return ValueTask.FromResult(DomainResult.Success());
    }
}

public sealed class RecordingTrackerPort : IDistributedTrackerPort
{
    public List<(SwarmRendezvousId Rendezvous, PublicIdentityId MemberId)> Unregistered { get; } = [];
    public List<(SwarmRendezvousId Rendezvous, PublicIdentityId MemberId)> Announced { get; } = [];

    public ValueTask<IReadOnlyList<DirectEndpointCandidate>> AnnounceAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        IReadOnlyList<DirectEndpointCandidate> candidates,
        SwarmPeerRole role,
        CancellationToken ct = default)
    {
        Announced.Add((rendezvousId, memberId));
        return ValueTask.FromResult(candidates);
    }

    public ValueTask UnregisterAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        CancellationToken ct = default)
    {
        Unregistered.Add((rendezvousId, memberId));
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeTransferStream : ITransferStream
{
    private readonly object _sync = new();
    public StreamIndex Index { get; set; } = StreamIndex.FromInt(0);
    public bool IsDisposed { get; private set; }
    public List<IStreamFramePayload> SentFrames { get; } = [];
    public Queue<IStreamFramePayload> InboundFrames { get; } = new();
    public Func<IStreamFramePayload, IStreamFramePayload?>? AutoResponder { get; set; }

    public ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        lock (_sync)
        {
            SentFrames.Add(frame);
            if (AutoResponder != null)
            {
                IStreamFramePayload? response = AutoResponder(frame);
                if (response != null)
                {
                    InboundFrames.Enqueue(response);
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            return ValueTask.FromResult(InboundFrames.Dequeue());
        }
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeTransferStreamPool : ITransferStreamPool
{
    public StreamCount ActiveStreams => StreamCount.FromInt(Streams.Count);
    public List<ITransferStream> Streams { get; } = [];
    public List<IStreamFramePayload> BroadcastFrames { get; } = [];
    public bool IsDisposed { get; private set; }

    public ValueTask ConnectDirectAsync(IReadOnlyList<DirectEndpointCandidate> candidates, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ConnectRelayPipeAsync(RelayRendezvousDescriptor rendezvous, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ITransferStream GetStream(StreamIndex index) => Streams[index.Value];

    public ValueTask BroadcastFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        BroadcastFrames.Add(frame);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryFilterPolicyRepository : IFileTransferFilterPolicyRepository
{
    public FileTransferFilterPolicy Policy { get; set; } = FileTransferFilterPolicy.CreateDefault();

    public ValueTask SavePolicyAsync(FileTransferFilterPolicy policy, CancellationToken ct = default)
    {
        Policy = policy;
        return ValueTask.CompletedTask;
    }

    public ValueTask<FileTransferFilterPolicy> GetPolicyAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        return ValueTask.FromResult(Policy);
    }
}

public sealed class RecordingTransferRateLimiter : ITransferRateLimiter
{
    private long _totalDownloadedBytes;
    private long _totalUploadedBytes;

    public long TotalDownloadedBytes => Interlocked.Read(ref _totalDownloadedBytes);
    public long TotalUploadedBytes => Interlocked.Read(ref _totalUploadedBytes);

    public ValueTask SetGlobalSpeedCapsAsync(BandwidthLimit downloadCap, BandwidthLimit uploadCap, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask SetSessionSpeedCapsAsync(TransferSessionId sessionId, BandwidthLimit downloadCap, BandwidthLimit uploadCap, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask ThrottleDownloadAsync(TransferSessionId sessionId, TransferByteLength byteCount, CancellationToken ct = default)
    {
        Interlocked.Add(ref _totalDownloadedBytes, byteCount.Bytes);
        return ValueTask.CompletedTask;
    }

    public ValueTask ThrottleUploadAsync(TransferSessionId sessionId, TransferByteLength byteCount, CancellationToken ct = default)
    {
        Interlocked.Add(ref _totalUploadedBytes, byteCount.Bytes);
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryBlobStorageService : IBlobStorageService
{
    private readonly ConcurrentDictionary<BlobId, byte[]> _blobs = new();

    public void StoreRawBlob(BlobId id, byte[] content) => _blobs[id] = content;

    public async ValueTask<DomainResult<BlobUploadResult>> UploadBlobAsync(
        BlobUploadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        using MemoryStream ms = new();
        await request.ContentStream.CopyToAsync(ms, ct);
        byte[] bytes = ms.ToArray();
        byte[] sha256 = SHA256.HashData(bytes);
        BlobId id = BlobId.FromSha256(sha256);
        _blobs[id] = bytes;

        var reference = new BlobReference
        {
            Id = id,
            CiphertextSha256 = sha256,
            EncryptionKey = new byte[32],
            BaseNonce = new byte[12],
            PlaintextSizeBytes = bytes.Length,
            CiphertextSizeBytes = bytes.Length + 16,
            MimeType = request.MimeType,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            StorageMode = BlobStorageMode.ChannelRelay,
            RelayEndpointUri = "https://relay.percolator.net/pipes"
        };

        return DomainResult<BlobUploadResult>.Success(new BlobUploadResult(reference, BlobStorageMode.ChannelRelay));
    }

    public ValueTask<DomainResult<BlobDownloadResult>> DownloadBlobAsync(
        BlobDownloadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_blobs.TryGetValue(request.Reference.Id, out byte[]? bytes))
            return ValueTask.FromResult(DomainResult<BlobDownloadResult>.Failure(new DomainError("Blob.NotFound", "Blob not found.")));

        Stream stream = new MemoryStream(bytes, writable: false);
        return ValueTask.FromResult(DomainResult<BlobDownloadResult>.Success(
            new BlobDownloadResult(stream, request.Reference.MimeType, bytes.Length)));
    }

    public bool IsCachedLocally(BlobId blobId) => _blobs.ContainsKey(blobId);

    public ValueTask DeleteLocalCacheAsync(BlobId blobId, CancellationToken ct = default)
    {
        _blobs.TryRemove(blobId, out _);
        return ValueTask.CompletedTask;
    }
}
