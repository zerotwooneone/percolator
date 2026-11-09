namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;

public sealed class StreamRegistryOptions
{
    public int DefaultQueueCapacity { get; set; } = 1000;
}

public interface IStreamRegistryManagement : IStreamRegistry
{
    ChannelReader<ReadOnlyMemory<byte>> RegisterStream(PublicIdentityId targetId, int? capacity = null);
    ChannelReader<ReadOnlyMemory<byte>> RegisterRelayStream(PublicIdentityId relayId, int? capacity = null);
    void UnregisterStream(PublicIdentityId targetId);
    void UnregisterRelayStream(PublicIdentityId relayId);
    void RegisterDirectEndpoint(PublicIdentityId targetId, Uri directEndpoint);
    void UnregisterDirectEndpoint(PublicIdentityId targetId);
    Uri? GetDirectEndpoint(PublicIdentityId targetId);
}

public sealed class StreamRegistry : IStreamRegistryManagement
{
    private readonly StreamRegistryOptions _options;
    private readonly ConcurrentDictionary<PublicIdentityId, Channel<ReadOnlyMemory<byte>>> _peerStreams = new();
    private readonly ConcurrentDictionary<PublicIdentityId, Channel<ReadOnlyMemory<byte>>> _relayStreams = new();
    private readonly ConcurrentDictionary<PublicIdentityId, Uri> _directEndpoints = new();

    public StreamRegistry(StreamRegistryOptions? options = null)
    {
        _options = options ?? new StreamRegistryOptions();
    }

    public bool HasActiveStream(PublicIdentityId targetId) => _peerStreams.ContainsKey(targetId);

    public bool HasActiveRelayStream(PublicIdentityId relayId) => _relayStreams.ContainsKey(relayId);

    public void RegisterDirectEndpoint(PublicIdentityId targetId, Uri directEndpoint)
    {
        ArgumentNullException.ThrowIfNull(directEndpoint);
        _directEndpoints[targetId] = directEndpoint;
    }

    public void UnregisterDirectEndpoint(PublicIdentityId targetId)
    {
        _directEndpoints.TryRemove(targetId, out _);
    }

    public Uri? GetDirectEndpoint(PublicIdentityId targetId)
    {
        return _directEndpoints.TryGetValue(targetId, out var uri) ? uri : null;
    }

    public ChannelReader<ReadOnlyMemory<byte>> RegisterStream(PublicIdentityId targetId, int? capacity = null)
    {
        int cap = capacity ?? _options.DefaultQueueCapacity;
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(cap)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _peerStreams[targetId] = channel;
        return channel.Reader;
    }

    public ChannelReader<ReadOnlyMemory<byte>> RegisterRelayStream(PublicIdentityId relayId, int? capacity = null)
    {
        int cap = capacity ?? _options.DefaultQueueCapacity;
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(cap)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _relayStreams[relayId] = channel;
        return channel.Reader;
    }

    public void UnregisterStream(PublicIdentityId targetId)
    {
        _directEndpoints.TryRemove(targetId, out _);
        if (_peerStreams.TryRemove(targetId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void UnregisterRelayStream(PublicIdentityId relayId)
    {
        if (_relayStreams.TryRemove(relayId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public ValueTask<StreamWriteResult> TryWriteAsync(
        PublicIdentityId targetId,
        ReadOnlyMemory<byte> framedPayload,
        CancellationToken ct = default)
    {
        if (!_peerStreams.TryGetValue(targetId, out var channel) &&
            !_relayStreams.TryGetValue(targetId, out channel))
        {
            return ValueTask.FromResult(StreamWriteResult.Closed("Stream not found or closed."));
        }

        // Attempt non-blocking write to bounded channel
        if (channel.Writer.TryWrite(framedPayload))
        {
            return ValueTask.FromResult(StreamWriteResult.Success());
        }

        // If TryWrite returned false, check if channel writer is closed or queue is saturated (backpressured)
        if (channel.Reader.Completion.IsCompleted)
        {
            return ValueTask.FromResult(StreamWriteResult.Closed("Channel reader completion has ended."));
        }

        return ValueTask.FromResult(StreamWriteResult.Backpressured());
    }
}
