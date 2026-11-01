namespace Percolator.Infrastructure.Plugins.BlobStorage;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;

public sealed class ChannelRelayResolver : IChannelRelayResolver
{
    private readonly ConcurrentDictionary<ChannelId, Uri> _channelRelayMap = new();
    private readonly Uri? _defaultRelayUri;

    public ChannelRelayResolver(Uri? defaultRelayUri = null)
    {
        _defaultRelayUri = defaultRelayUri;
    }

    public void RegisterRelayForChannel(ChannelId channelId, Uri relayUri)
    {
        ArgumentNullException.ThrowIfNull(relayUri);
        _channelRelayMap[channelId] = relayUri;
    }

    public ValueTask<Uri?> ResolveRelayForChannelAsync(ChannelId channelId, CancellationToken ct = default)
    {
        if (_channelRelayMap.TryGetValue(channelId, out var uri))
        {
            return ValueTask.FromResult<Uri?>(uri);
        }

        return ValueTask.FromResult(_defaultRelayUri);
    }
}
