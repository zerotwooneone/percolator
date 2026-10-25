using System.Collections.Concurrent;
using Percolator.Application2.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests.Adapters;

public sealed class InMemoryRelayBlobStore
{
    private readonly ConcurrentDictionary<BlobId, byte[]> _blobs = new();

    public void SaveBlob(BlobId blobId, byte[] ciphertext)
    {
        _blobs[blobId] = ciphertext;
    }

    public bool TryGetBlob(BlobId blobId, out byte[] ciphertext)
    {
        return _blobs.TryGetValue(blobId, out ciphertext!);
    }

    public bool DeleteBlob(BlobId blobId)
    {
        return _blobs.TryRemove(blobId, out _);
    }

    public bool Contains(BlobId blobId) => _blobs.ContainsKey(blobId);
    public bool HasBlob(BlobId blobId) => Contains(blobId);
    public byte[]? GetBlob(BlobId blobId) => TryGetBlob(blobId, out var b) ? b : null;
}

public sealed class LoopbackRelayBlobClient : IRelayBlobClient
{
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;

    public LoopbackRelayBlobClient(InMemoryLoopbackNetworkSwitch networkSwitch)
    {
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
    }

    public async ValueTask<DomainResult<string>> UploadBlobAsync(
        Uri relayUri,
        BlobId blobId,
        Stream ciphertextStream,
        ChannelId channelId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayUri);
        ArgumentNullException.ThrowIfNull(ciphertextStream);

        // 1. Resolve relay from registered channel, or matching port in URI
        RelayNode? relay = null;
        if (!_networkSwitch.TryGetRelayForChannel(channelId, out relay))
        {
            relay = _networkSwitch.AllRelays.FirstOrDefault(r => r.Port.Value == relayUri.Port);
        }

        if (relay is null)
        {
            // If only one relay exists in the cluster, use it
            relay = _networkSwitch.AllRelays.FirstOrDefault();
        }

        if (relay is null)
        {
            return DomainResult<string>.Failure(new DomainError(
                "RELAY_NOT_FOUND", $"Relay not found for URI {relayUri}."));
        }

        using var memory = new MemoryStream();
        await ciphertextStream.CopyToAsync(memory, ct).ConfigureAwait(false);
        var bytes = memory.ToArray();

        relay.BlobStore.SaveBlob(blobId, bytes);

        return DomainResult<string>.Success(relayUri.ToString());
    }

    public ValueTask<DomainResult<Stream>> DownloadBlobAsync(
        Uri relayUri,
        BlobId blobId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayUri);

        RelayNode? relay = _networkSwitch.AllRelays.FirstOrDefault(r => r.Port.Value == relayUri.Port)
                          ?? _networkSwitch.AllRelays.FirstOrDefault();

        if (relay is null)
        {
            return ValueTask.FromResult(DomainResult<Stream>.Failure(new DomainError(
                "RELAY_NOT_FOUND", $"Relay not found for URI {relayUri}.")));
        }

        if (!relay.BlobStore.TryGetBlob(blobId, out var bytes))
        {
            return ValueTask.FromResult(DomainResult<Stream>.Failure(new DomainError(
                "BLOB_NOT_FOUND", $"Blob {blobId} not found on relay.")));
        }

        Stream stream = new MemoryStream(bytes);
        return ValueTask.FromResult(DomainResult<Stream>.Success(stream));
    }

    public ValueTask<DomainResult> DeleteBlobAsync(
        Uri relayUri,
        BlobId blobId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayUri);

        RelayNode? relay = _networkSwitch.AllRelays.FirstOrDefault(r => r.Port.Value == relayUri.Port)
                          ?? _networkSwitch.AllRelays.FirstOrDefault();

        if (relay is not null)
        {
            relay.BlobStore.DeleteBlob(blobId);
        }

        return ValueTask.FromResult(DomainResult.Success());
    }
}

public sealed class InMemoryLocalChunkStore : ILocalChunkStore
{
    private readonly ConcurrentDictionary<BlobId, byte[]> _store = new();

    public ValueTask<bool> HasBlobAsync(BlobId blobId, CancellationToken ct = default) =>
        ValueTask.FromResult(_store.ContainsKey(blobId));

    public ValueTask<Stream> OpenReadAsync(BlobId blobId, CancellationToken ct = default)
    {
        if (_store.TryGetValue(blobId, out var bytes))
        {
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes));
        }

        throw new KeyNotFoundException($"Blob {blobId} was not found in local chunk store.");
    }

    public async ValueTask SaveBlobAsync(BlobId blobId, Stream contentStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contentStream);
        using var ms = new MemoryStream();
        await contentStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        _store[blobId] = ms.ToArray();
    }

    public ValueTask DeleteBlobAsync(BlobId blobId, CancellationToken ct = default)
    {
        _store.TryRemove(blobId, out _);
        return ValueTask.CompletedTask;
    }
}

public sealed class LoopbackChannelRelayResolver : IChannelRelayResolver
{
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;
    private readonly IChannelRepository _channelRepo;
    private readonly IPeerMailboxRoutingRepository _mailboxRoutingRepo;
    private readonly IDateTimeProvider _timeProvider;

    public LoopbackChannelRelayResolver(
        InMemoryLoopbackNetworkSwitch networkSwitch,
        IChannelRepository channelRepo,
        IPeerMailboxRoutingRepository mailboxRoutingRepo,
        IDateTimeProvider timeProvider)
    {
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _mailboxRoutingRepo = mailboxRoutingRepo ?? throw new ArgumentNullException(nameof(mailboxRoutingRepo));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<Uri?> ResolveRelayForChannelAsync(ChannelId channelId, CancellationToken ct = default)
    {
        // 1. Direct channel mapping registered on network switch
        if (_networkSwitch.TryGetRelayForChannel(channelId, out var relay) && relay is not null)
        {
            return new Uri($"percolator://localhost:{relay.Port.Value}");
        }

        // 2. Direct channel -> resolve remote peer and lookup their specific mailbox relay
        var directChannel = await _channelRepo.GetDirectByIdAsync(channelId, ct).ConfigureAwait(false);
        if (directChannel is not null)
        {
            var route = await _mailboxRoutingRepo.GetBestRouteAsync(directChannel.RemotePeerId, _timeProvider.UtcNow, ct).ConfigureAwait(false);
            if (route?.RelayEndpoint is not null)
            {
                return new Uri($"percolator://{route.RelayEndpoint.Host}:{route.RelayEndpoint.Port}");
            }
        }

        return null;
    }
}

public sealed class LoopbackPeerStreamBlobClient : IPeerStreamBlobClient
{
    private readonly PublicIdentityId _localIdentityId;
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;

    public LoopbackPeerStreamBlobClient(
        PublicIdentityId localIdentityId,
        InMemoryLoopbackNetworkSwitch networkSwitch)
    {
        _localIdentityId = localIdentityId;
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
    }

    public async ValueTask<DomainResult<Stream>> RequestBlobFromPeerAsync(
        PublicIdentityId peerId,
        BlobId blobId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await _networkSwitch.TransferBlobAcrossStreamAsync(_localIdentityId, peerId, blobId, ct).ConfigureAwait(false);
    }
}
