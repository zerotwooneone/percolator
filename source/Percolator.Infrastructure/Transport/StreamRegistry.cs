using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Percolator.Domain.Identities.ValueObjects;

public sealed class StreamRegistryOptions
{
    public int DefaultQueueCapacity { get; set; } = 1000;
}

public interface IStreamRegistryManagement : IStreamRegistry
{
    ChannelReader<ReadOnlyMemory<byte>> RegisterStream(Domain.Identities.PublicIdentityId targetId, int? capacity = null);
    ChannelReader<ReadOnlyMemory<byte>> RegisterRelayStream(Domain.Identities.PublicIdentityId relayId, int? capacity = null);
    void UnregisterStream(Domain.Identities.PublicIdentityId targetId);
    void UnregisterRelayStream(Domain.Identities.PublicIdentityId relayId);
    void RegisterDirectEndpoint(Domain.Identities.PublicIdentityId targetId, Uri directEndpoint);
    void UnregisterDirectEndpoint(Domain.Identities.PublicIdentityId targetId);
    Uri? GetDirectEndpoint(Domain.Identities.PublicIdentityId targetId);
}

public sealed class StreamRegistry : IStreamRegistryManagement
{
    private readonly StreamRegistryOptions _options;
    private readonly ConcurrentDictionary<Domain.Identities.PublicIdentityId, Channel<ReadOnlyMemory<byte>>> _peerStreams = new();
    private readonly ConcurrentDictionary<Domain.Identities.PublicIdentityId, Channel<ReadOnlyMemory<byte>>> _relayStreams = new();
    private readonly ConcurrentDictionary<Domain.Identities.PublicIdentityId, Uri> _directEndpoints = new();

    public StreamRegistry(StreamRegistryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool HasActiveStream(Domain.Identities.PublicIdentityId targetId) => _peerStreams.ContainsKey(targetId);

    public bool HasActiveRelayStream(Domain.Identities.PublicIdentityId relayId) => _relayStreams.ContainsKey(relayId);

    public void RegisterDirectEndpoint(Domain.Identities.PublicIdentityId targetId, Uri directEndpoint)
    {
        ArgumentNullException.ThrowIfNull(directEndpoint);
        _directEndpoints[targetId] = directEndpoint;
    }

    public void UnregisterDirectEndpoint(Domain.Identities.PublicIdentityId targetId)
    {
        _directEndpoints.TryRemove(targetId, out _);
    }

    public Uri? GetDirectEndpoint(Domain.Identities.PublicIdentityId targetId)
    {
        return _directEndpoints.TryGetValue(targetId, out var uri) ? uri : null;
    }

    public ChannelReader<ReadOnlyMemory<byte>> RegisterStream(Domain.Identities.PublicIdentityId targetId, int? capacity = null)
    {
        int cap = capacity ?? _options.DefaultQueueCapacity;
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(cap)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _peerStreams[targetId] = channel;
        return channel.Reader;
    }

    public ChannelReader<ReadOnlyMemory<byte>> RegisterRelayStream(Domain.Identities.PublicIdentityId relayId, int? capacity = null)
    {
        int cap = capacity ?? _options.DefaultQueueCapacity;
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(cap)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

        _relayStreams[relayId] = channel;
        return channel.Reader;
    }

    public void UnregisterStream(Domain.Identities.PublicIdentityId targetId)
    {
        _directEndpoints.TryRemove(targetId, out _);
        if (_peerStreams.TryRemove(targetId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void UnregisterRelayStream(Domain.Identities.PublicIdentityId relayId)
    {
        if (_relayStreams.TryRemove(relayId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public ValueTask<StreamWriteResult> TryWriteAsync(
        Domain.Identities.PublicIdentityId targetId,
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
