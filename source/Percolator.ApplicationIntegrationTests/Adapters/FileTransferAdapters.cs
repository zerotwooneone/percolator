using System.Collections.Concurrent;
using System.Threading.Channels;
using Percolator.Application2IntegrationTests.Infrastructure;
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

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class LoopbackTransferStream : ITransferStream
{
    private readonly ChannelWriter<IStreamFramePayload> _outgoing;
    private readonly ChannelReader<IStreamFramePayload> _incoming;

    public StreamIndex Index { get; }

    public LoopbackTransferStream(
        StreamIndex index,
        ChannelWriter<IStreamFramePayload> outgoing,
        ChannelReader<IStreamFramePayload> incoming)
    {
        Index = index;
        _outgoing = outgoing;
        _incoming = incoming;
    }

    public async ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        await _outgoing.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    public async ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default)
    {
        return await _incoming.ReadAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _outgoing.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public sealed class LoopbackStreamPoolAdapter : ITransferStreamPool
{
    private readonly EndpointPort _localPort;
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;
    private readonly ConcurrentDictionary<int, LoopbackTransferStream> _streams = new();
    private readonly StreamCount _streamCount;

    public StreamCount ActiveStreams => _streamCount;

    public LoopbackStreamPoolAdapter(
        EndpointPort localPort,
        InMemoryLoopbackNetworkSwitch networkSwitch,
        StreamCount? streamCount = null)
    {
        _localPort = localPort;
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
        _streamCount = streamCount ?? StreamCount.Default;
    }

    public ValueTask ConnectDirectAsync(IReadOnlyList<DirectEndpointCandidate> candidates, CancellationToken ct = default)
    {
        if (candidates == null || candidates.Count == 0)
            return ValueTask.CompletedTask;

        var target = candidates[0];
        var remotePort = new EndpointPort(target.Port.Value);

        for (int i = 0; i < _streamCount.Count; i++)
        {
            var (outgoing, incoming) = _networkSwitch.GetOrCreateStreamPair(_localPort, remotePort, i);
            _streams[i] = new LoopbackTransferStream(StreamIndex.FromInt(i), outgoing, incoming);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ConnectRelayPipeAsync(RelayRendezvousDescriptor rendezvous, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ITransferStream GetStream(StreamIndex index)
    {
        if (_streams.TryGetValue(index.Value, out var stream))
        {
            return stream;
        }

        throw new InvalidOperationException($"Stream at index {index.Value} has not been connected.");
    }

    public async ValueTask BroadcastFrameAsync(IStreamFramePayload frame, CancellationToken ct = default)
    {
        for (int i = 0; i < _streams.Count; i++)
        {
            if (_streams.TryGetValue(i, out var stream))
            {
                await stream.SendFrameAsync(frame, ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stream in _streams.Values)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        _streams.Clear();
    }
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
    public Bitfield? ConfiguredResumptionBitfield { get; set; }
    public TotalBundleBytes? LastCheckedBytes { get; private set; }
    public List<ResolvedFilePath> FinalizedFiles { get; } = [];
    public Dictionary<string, byte[]> FilesOnDisk { get; } = [];
    private readonly Dictionary<(TransferSessionId, ChunkIndex, FileIndex), byte[]> _boundaryParts = new();

    public void StageFile(ResolvedFilePath path, byte[] contents)
    {
        lock (_sync)
        {
            FilesOnDisk[path.AbsolutePath] = contents;
        }
    }

    public byte[]? GetFileBytes(ResolvedFilePath path)
    {
        lock (_sync)
        {
            return FilesOnDisk.TryGetValue(path.AbsolutePath, out var bytes) ? bytes : null;
        }
    }

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

            return ValueTask.FromResult(FileMetadataResult.NotFound());
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

public sealed class InMemoryFilterPolicyRepository : IFileTransferFilterPolicyRepository
{
    private readonly PublicIdentityId _ownerId;
    private readonly ConcurrentDictionary<PublicIdentityId, FileTransferFilterPolicy> _policies = new();

    public InMemoryFilterPolicyRepository(PublicIdentityId ownerId)
    {
        _ownerId = ownerId;
    }

    public ValueTask SavePolicyAsync(FileTransferFilterPolicy policy, CancellationToken ct = default)
    {
        _policies[policy.OwnerId] = policy;
        return ValueTask.CompletedTask;
    }

    public ValueTask<FileTransferFilterPolicy> GetPolicyAsync(PublicIdentityId ownerId, CancellationToken ct = default)
    {
        return ValueTask.FromResult(_policies.TryGetValue(ownerId, out var p) ? p : FileTransferFilterPolicy.DefaultPermissive(_ownerId));
    }
}

public sealed class LoopbackDistributedTrackerPort : IDistributedTrackerPort
{
    private readonly ConcurrentDictionary<SwarmRendezvousId, ConcurrentDictionary<PublicIdentityId, (IReadOnlyList<DirectEndpointCandidate> Candidates, SwarmPeerRole Role)>> _swarms = new();

    public ValueTask<IReadOnlyList<DirectEndpointCandidate>> AnnounceAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        IReadOnlyList<DirectEndpointCandidate> candidates,
        SwarmPeerRole role,
        CancellationToken ct = default)
    {
        var members = _swarms.GetOrAdd(rendezvousId, _ => new());
        members[memberId] = (candidates, role);

        List<DirectEndpointCandidate> peers = [];
        foreach (var kvp in members)
        {
            if (kvp.Key != memberId)
            {
                peers.AddRange(kvp.Value.Candidates);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<DirectEndpointCandidate>>(peers);
    }

    public ValueTask UnregisterAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        CancellationToken ct = default)
    {
        if (_swarms.TryGetValue(rendezvousId, out var members))
        {
            members.TryRemove(memberId, out _);
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class LoopbackNetworkEndpointDiscoveryPort : INetworkEndpointDiscoveryPort
{
    private readonly DirectEndpointCandidate _candidate;

    public LoopbackNetworkEndpointDiscoveryPort(DirectEndpointCandidate candidate)
    {
        _candidate = candidate;
    }

    public ValueTask<IReadOnlyList<DirectEndpointCandidate>> DiscoverLocalCandidatesAsync(
        DirectWanPolicy wanPolicy,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<DirectEndpointCandidate>>([_candidate]);
    }

    public ValueTask<RelayRendezvousDescriptor?> AllocateRelayRendezvousAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<RelayRendezvousDescriptor?>(null);
    }
}
