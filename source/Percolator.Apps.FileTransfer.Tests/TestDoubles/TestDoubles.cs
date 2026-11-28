using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
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
using Percolator.PluginSdk.Events;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

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

    public ValueTask<IReadOnlyList<TransferSessionId>> GetExpiredPendingOfferIdsAsync(DateTimeOffset createdBeforeUtc, CancellationToken ct = default)
    {
        var expired = _sessions.Values
            .Where(s => s.State == TransferSessionState.PendingConsent && s.CreatedAtUtc < createdBeforeUtc)
            .Select(s => s.Id)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<TransferSessionId>>(expired);
    }
}

public sealed class InMemoryTransferSessionTransportStore : ITransferSessionTransportStore
{
    private readonly ConcurrentDictionary<TransferSessionId, TransferSessionTransportContext> _transportContexts = new();

    public ValueTask SaveTransportContextAsync(TransferSessionTransportContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _transportContexts[context.SessionId] = context;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TransferSessionTransportContext?> GetTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        _transportContexts.TryGetValue(sessionId, out var ctx);
        return ValueTask.FromResult<TransferSessionTransportContext?>(ctx);
    }

    public ValueTask DeleteTransportContextAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        _transportContexts.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }
}

public sealed record CheckpointRecord(
    TransferSessionId SessionId,
    Bitfield LocalBitfield,
    TransferSessionState State,
    TotalBundleBytes UploadedBytes,
    DateTimeOffset? CompletedAtUtc);

public sealed class InMemoryTransferSessionCheckpointStore : ITransferSessionCheckpointStore
{
    private readonly object _sync = new();
    public List<CheckpointRecord> Checkpoints { get; } = [];

    public ValueTask SaveCheckpointAsync(
        TransferSessionId sessionId,
        Bitfield localBitfield,
        TransferSessionState state,
        TotalBundleBytes uploadedBytes,
        DateTimeOffset? completedAtUtc,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            Checkpoints.Add(new CheckpointRecord(sessionId, localBitfield, state, uploadedBytes, completedAtUtc));
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryDesktopFileStorage : IDesktopFileStorage
{
    private readonly object _sync = new();
    public DiskSpacePreflightResult DiskSpaceResult { get; set; } = DiskSpacePreflightResult.Sufficient();
    public Bitfield? ConfiguredResumptionBitfield { get; set; }
    public TotalBundleBytes? LastCheckedBytes { get; private set; }
    public List<ResolvedFilePath> FinalizedFiles { get; } = [];
    public Dictionary<string, byte[]> FilesOnDisk { get; } = [];

    public byte[]? GetFileBytes(ResolvedFilePath path)
    {
        lock (_sync)
        {
            return FilesOnDisk.TryGetValue(path.AbsolutePath, out var bytes) ? bytes : null;
        }
    }
    private readonly Dictionary<(TransferSessionId, ChunkIndex, FileIndex), byte[]> _boundaryParts = new();

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

    public ValueTask<int> ReadChunkIntoAsync(ResolvedFilePath sourcePath, FileByteOffset fileOffset, Memory<byte> destination, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!FilesOnDisk.TryGetValue(sourcePath.AbsolutePath, out var buffer))
            {
                return ValueTask.FromResult(0);
            }

            int available = Math.Max(0, buffer.Length - (int)fileOffset.Offset);
            int copyLen = Math.Min(destination.Length, available);
            if (copyLen > 0)
            {
                buffer.AsSpan((int)fileOffset.Offset, copyLen).CopyTo(destination.Span);
            }
            return ValueTask.FromResult(copyLen);
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
        return ValueTask.FromResult(ConfiguredResumptionBitfield ?? Bitfield.Empty(manifest.TotalChunks));
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

    public ValueTask WriteBoundarySliceAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, FileIndex fileIndex, PlaintextChunkData data, CancellationToken ct = default)
    {
        lock (_sync)
        {
            _boundaryParts[(sessionId, chunkIndex, fileIndex)] = data.Span.ToArray();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<PlaintextChunkData?> ReadBoundarySliceAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, FileIndex fileIndex, TransferByteLength length, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_boundaryParts.TryGetValue((sessionId, chunkIndex, fileIndex), out var bytes))
            {
                return ValueTask.FromResult<PlaintextChunkData?>(PlaintextChunkData.FromBytes(bytes));
            }
            return ValueTask.FromResult<PlaintextChunkData?>(null);
        }
    }

    public ValueTask DeleteBoundaryPartsAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            var keysToRemove = _boundaryParts.Keys.Where(k => k.Item1 == sessionId).ToList();
            foreach (var key in keysToRemove)
            {
                _boundaryParts.Remove(key);
            }
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class RecordingPayloadSender : IPayloadSender
{
    public List<OutboundPayloadContext> SentPayloads { get; } = [];
    public DomainResult ResultToReturn { get; set; } = DomainResult.Success();

    public ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        SentPayloads.Add(context);
        return ValueTask.FromResult(ResultToReturn);
    }
}

public sealed class RecordingTrackerPort : IDistributedTrackerPort
{
    public List<(SwarmRendezvousId Rendezvous, EphemeralSwarmPeerId EphemeralPeerId)> Unregistered { get; } = [];
    public List<(SwarmRendezvousId Rendezvous, EphemeralSwarmPeerId EphemeralPeerId, SwarmPeerRole Role)> Announced { get; } = [];
    public List<EphemeralSwarmPeerId> PeersToReturn { get; } = [];
    public StunEndpointDescriptor? StunEndpoint { get; set; }
    public bool SupportsTransferPipe { get; set; }

    public ValueTask<SwarmAnnounceResult> AnnounceAsync(
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        SwarmPeerRole role,
        Percolator.Domain.Delivery.ValueObjects.RelayEndpoint? exchangeEndpoint = null,
        CancellationToken ct = default)
    {
        Announced.Add((rendezvousId, ephemeralPeerId, role));
        return ValueTask.FromResult(new SwarmAnnounceResult(
            PeersToReturn.ToList(),
            StunEndpoint,
            SupportsTransferPipe));
    }

    public ValueTask UnregisterAsync(
        SwarmRendezvousId rendezvousId,
        EphemeralSwarmPeerId ephemeralPeerId,
        Percolator.Domain.Delivery.ValueObjects.RelayEndpoint? exchangeEndpoint = null,
        CancellationToken ct = default)
    {
        Unregistered.Add((rendezvousId, ephemeralPeerId));
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeNetworkEndpointDiscoveryPort : Percolator.Apps.FileTransfer.Ports.INetworkEndpointDiscoveryPort
{
    public List<DirectEndpointCandidate> LocalCandidates { get; set; } =
    [
        new DirectEndpointCandidate(EndpointHost.Parse("192.168.1.50"), PortNumber.FromInt(49152), DirectCandidateKind.DirectLan)
    ];

    public RelayRendezvousDescriptor? AllocatedRelay { get; set; } =
        new RelayRendezvousDescriptor(RelayPipeEndpointUri.Parse("https://exchange.test.local/pipe/session"), RelayPipeToken.Generate());

    public ValueTask<IReadOnlyList<DirectEndpointCandidate>> DiscoverLocalCandidatesAsync(DirectWanPolicy wanPolicy, CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<DirectEndpointCandidate>>(LocalCandidates);
    }

    public ValueTask<RelayRendezvousDescriptor?> AllocateRelayRendezvousAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        return ValueTask.FromResult(AllocatedRelay);
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
    private readonly object _sync = new();
    public StreamCount ActiveStreams => StreamCount.FromInt(Streams.Count);
    public List<ITransferStream> Streams { get; } = [];
    public List<IStreamFramePayload> BroadcastFrames { get; } = [];
    public List<DirectEndpointCandidate> ConnectedDirectCandidates { get; } = [];
    public RelayRendezvousDescriptor? ConnectedRelayRendezvous { get; private set; }
    public bool SimulateDirectConnectionFailure { get; set; }
    public bool IsDisposed { get; private set; }

    public ValueTask ConnectDirectAsync(IReadOnlyList<DirectEndpointCandidate> candidates, CancellationToken ct = default)
    {
        if (SimulateDirectConnectionFailure)
            throw new IOException("Simulated symmetric NAT / firewall timeout.");

        ConnectedDirectCandidates.AddRange(candidates);
        return ValueTask.CompletedTask;
    }

    public ValueTask ConnectRelayPipeAsync(RelayRendezvousDescriptor rendezvous, CancellationToken ct = default)
    {
        ConnectedRelayRendezvous = rendezvous;
        return ValueTask.CompletedTask;
    }

    public ITransferStream GetStream(StreamIndex index) => Streams[index.Value];

    public ValueTask BroadcastFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        lock (_sync)
        {
            BroadcastFrames.Add(frame);
        }
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
    private readonly ConcurrentDictionary<PublicIdentityId, FileTransferFilterPolicy> _policies = new();

    public ValueTask SavePolicyAsync(FileTransferFilterPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policies[policy.OwnerId] = policy;
        return ValueTask.CompletedTask;
    }

    public ValueTask<FileTransferFilterPolicy> GetPolicyAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        return ValueTask.FromResult(_policies.TryGetValue(ownerId, out FileTransferFilterPolicy? p)
            ? p
            : FileTransferFilterPolicy.DefaultPermissive(ownerId));
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
            RelayEndpointUri = "https://exchange.test.local/blobs"
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

public sealed class InMemoryTransferSessionCertificateStore : ITransferSessionCertificateStore
{
    private readonly ConcurrentDictionary<TransferSessionId, (CertSha256Fingerprint Fingerprint, byte[] CertDer, byte[] KeyPkcs8)> _store = new();
    public List<TransferSessionId> DeletedSessions { get; } = [];

    public ValueTask<TransferSessionCertificateMaterial> GetOrCreateAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        var entry = _store.GetOrAdd(sessionId, _ =>
        {
            byte[] fp = RandomNumberGenerator.GetBytes(32);
            byte[] cert = RandomNumberGenerator.GetBytes(64);
            byte[] key = RandomNumberGenerator.GetBytes(48);
            return (CertSha256Fingerprint.FromBytes(fp), cert, key);
        });

        return ValueTask.FromResult(new TransferSessionCertificateMaterial(
            entry.Fingerprint,
            entry.CertDer.ToArray(),
            entry.KeyPkcs8.ToArray()));
    }

    public ValueTask<TransferSessionCertificateMaterial?> GetAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        if (!_store.TryGetValue(sessionId, out var entry))
        {
            return ValueTask.FromResult<TransferSessionCertificateMaterial?>(null);
        }

        return ValueTask.FromResult<TransferSessionCertificateMaterial?>(new TransferSessionCertificateMaterial(
            entry.Fingerprint,
            entry.CertDer.ToArray(),
            entry.KeyPkcs8.ToArray()));
    }

    public ValueTask DeleteAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        _store.TryRemove(sessionId, out _);
        DeletedSessions.Add(sessionId);
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryEventBusForTests : IEventPublisher, IEventSubscriber
{
    private readonly object _sync = new();
    private readonly Dictionary<Type, List<Func<IApplicationEvent, CancellationToken, ValueTask>>> _handlers = [];

    public int SubscriptionCount
    {
        get
        {
            lock (_sync)
            {
                return _handlers.Values.Sum(l => l.Count);
            }
        }
    }

    public async ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IApplicationEvent
    {
        List<Func<IApplicationEvent, CancellationToken, ValueTask>> snapshot;
        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                return;
            }
            snapshot = list.ToList();
        }

        foreach (var handler in snapshot)
        {
            await handler(@event, cancellationToken);
        }
    }

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IApplicationEvent
    {
        Func<IApplicationEvent, CancellationToken, ValueTask> wrapper = (ev, ct) => handler((TEvent)ev, ct);
        lock (_sync)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _handlers[typeof(TEvent)] = list;
            }
            list.Add(wrapper);
        }

        return new Unsubscriber(() =>
        {
            lock (_sync)
            {
                if (_handlers.TryGetValue(typeof(TEvent), out var list))
                {
                    list.Remove(wrapper);
                }
            }
        });
    }

    public IDisposable RegisterFaultHandler(Action<EventDispatchError> handler) => new Unsubscriber(() => { });

    private sealed class Unsubscriber(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}

public sealed class InMemoryPairedTransferDataChannel : ITransferDataChannel
{
    private readonly ChannelWriter<IStreamFramePayload> _outbound;
    private readonly ChannelReader<IStreamFramePayload> _inbound;
    private int _disposed;

    public StreamIndex Index => StreamIndex.FromInt(0);
    public string Label => "test-paired-dc";
    public bool IsOpen => Volatile.Read(ref _disposed) == 0;
    public long BufferedAmount => 0;
    public TransferTransportKind TransportKind { get; }
    public CertSha256Fingerprint VerifiedRemoteCertSha256 { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private InMemoryPairedTransferDataChannel(
        ChannelWriter<IStreamFramePayload> outbound,
        ChannelReader<IStreamFramePayload> inbound,
        CertSha256Fingerprint verifiedRemoteCertSha256,
        TransferTransportKind transportKind)
    {
        _outbound = outbound;
        _inbound = inbound;
        VerifiedRemoteCertSha256 = verifiedRemoteCertSha256;
        TransportKind = transportKind;
    }

    public static (InMemoryPairedTransferDataChannel SideA, InMemoryPairedTransferDataChannel SideB) CreatePair(
        CertSha256Fingerprint certOfSideA,
        CertSha256Fingerprint certOfSideB,
        TransferTransportKind kind = TransferTransportKind.DirectWebRtcDtls)
    {
        var aToB = Channel.CreateUnbounded<IStreamFramePayload>();
        var bToA = Channel.CreateUnbounded<IStreamFramePayload>();

        var sideA = new InMemoryPairedTransferDataChannel(aToB.Writer, bToA.Reader, certOfSideB, kind);
        var sideB = new InMemoryPairedTransferDataChannel(bToA.Writer, aToB.Reader, certOfSideA, kind);
        return (sideA, sideB);
    }

    public async ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(InMemoryPairedTransferDataChannel));

        if (frame is ChunkDataFrame chunkData)
        {
            byte[] copy = chunkData.Data.Span.ToArray();
            frame = new ChunkDataFrame(chunkData.ChunkIndex, PlaintextChunkData.FromBytes(copy));
        }

        await _outbound.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    public async ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(InMemoryPairedTransferDataChannel));

        return await _inbound.ReadAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _outbound.TryComplete();
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeTransferPeerConnection : ITransferPeerConnection
{
    public TransferSessionId SessionId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public TransferTransportKind TransportKind => TransferTransportKind.DirectWebRtcDtls;
    public CertSha256Fingerprint? LocalDtlsFingerprint { get; }
    public CertSha256Fingerprint? RemoteDtlsFingerprint { get; private set; }
    public bool IsDisposed { get; private set; }
    public List<string> AppliedAnswers { get; } = [];
    public List<(string Candidate, string Mid)> AddedCandidates { get; } = [];

    private readonly TaskCompletionSource<ITransferDataChannel> _channelTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeTransferPeerConnection(
        TransferSessionId sessionId,
        PublicIdentityId remotePeerId,
        CertSha256Fingerprint localFingerprint)
    {
        SessionId = sessionId;
        RemotePeerId = remotePeerId;
        LocalDtlsFingerprint = localFingerprint;
    }

    public void CompleteChannel(ITransferDataChannel channel) => _channelTcs.TrySetResult(channel);

    public ValueTask<string> CreateOfferAsync(CancellationToken ct = default) =>
        ValueTask.FromResult($"v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 {LocalDtlsFingerprint!.ToColonHexUpper()}\r\n");

    public ValueTask<string> AcceptOfferAndCreateAnswerAsync(
        string remoteSdpOffer,
        CertSha256Fingerprint? expectedRemoteDtlsFingerprint,
        CancellationToken ct = default)
    {
        RemoteDtlsFingerprint = expectedRemoteDtlsFingerprint;
        return ValueTask.FromResult($"v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 {LocalDtlsFingerprint!.ToColonHexUpper()}\r\n");
    }

    public ValueTask ApplyRemoteAnswerAsync(
        string remoteSdpAnswer,
        CertSha256Fingerprint? expectedRemoteDtlsFingerprint,
        CancellationToken ct = default)
    {
        RemoteDtlsFingerprint = expectedRemoteDtlsFingerprint;
        AppliedAnswers.Add(remoteSdpAnswer);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddRemoteIceCandidateAsync(string candidate, string mid, CancellationToken ct = default)
    {
        AddedCandidates.Add((candidate, mid));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<ITransferDataChannel> WaitForAuthenticatedChannelAsync(
        StreamAuthToken expectedAuthToken,
        PublicIdentityId localIdentityId,
        CancellationToken ct = default)
    {
        return await _channelTcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        _channelTcs.TrySetCanceled();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeTransferPeerTransport : ITransferPeerTransport
{
    private readonly CertSha256Fingerprint _localFingerprint;
    public List<FakeTransferPeerConnection> CreatedConnections { get; } = [];
    public List<TransferSessionId> ClosedSessionIds { get; } = [];
    public int CloseAllCallCount { get; private set; }
    public Func<RelayedPipeConnectionOptions, ITransferDataChannel>? PipeChannelFactory { get; set; }

    public int ActiveConnectionCount => CreatedConnections.Count(c => !c.IsDisposed);

    public FakeTransferPeerTransport(CertSha256Fingerprint? localFingerprint = null)
    {
        _localFingerprint = localFingerprint ?? CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
    }

    public ValueTask<ITransferPeerConnection> CreateDirectConnectionAsync(
        DirectPeerConnectionOptions options,
        CancellationToken ct = default)
    {
        var conn = new FakeTransferPeerConnection(options.SessionId, options.RemotePeerId, _localFingerprint);
        CreatedConnections.Add(conn);
        return ValueTask.FromResult<ITransferPeerConnection>(conn);
    }

    public ValueTask<ITransferDataChannel> OpenRelayedPipeChannelAsync(
        RelayedPipeConnectionOptions options,
        CancellationToken ct = default)
    {
        if (!options.ExchangeSupportsTransferPipe)
        {
            throw new TransferTransportException(
                TransferTransportErrorCodes.TransferPipeUnavailable,
                "Exchange does not advertise TransferPipe.");
        }

        if (PipeChannelFactory != null)
        {
            return ValueTask.FromResult(PipeChannelFactory(options));
        }

        throw new TransferTransportException(
            TransferTransportErrorCodes.TransferPipeUnavailable,
            "No fake pipe channel configured.");
    }

    public async ValueTask CloseSessionConnectionsAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        ClosedSessionIds.Add(sessionId);
        foreach (var conn in CreatedConnections.Where(c => c.SessionId == sessionId))
        {
            await conn.DisposeAsync();
        }
    }

    public async ValueTask CloseAllConnectionsAsync(CancellationToken ct = default)
    {
        CloseAllCallCount++;
        foreach (var conn in CreatedConnections)
        {
            await conn.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => CloseAllConnectionsAsync();
}

public sealed class DirectBackgroundPayloadDispatcher : IBackgroundPayloadDispatcher
{
    private readonly IPayloadSender _sender;

    public DirectBackgroundPayloadDispatcher(IPayloadSender sender) => _sender = sender;

    public ValueTask<DomainResult> DispatchAsync(OutboundPayloadContext context, CancellationToken ct = default) =>
        _sender.SendPayloadAsync(context, ct);
}
