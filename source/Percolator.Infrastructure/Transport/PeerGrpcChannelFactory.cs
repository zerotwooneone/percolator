namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using Grpc.Net.Client;
using Percolator.Domain.Common;

public enum ChannelPoolKind
{
    Unauthenticated = 0,
    Authenticated = 1
}

public sealed class PeerGrpcChannelFactoryOptions
{
    public int MaxCapacity { get; set; } = 100;
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

public interface IPeerGrpcChannelFactory : IDisposable
{
    GrpcChannel GetOrCreateChannel(Uri address, ChannelPoolKind poolKind = ChannelPoolKind.Unauthenticated);
    void InvalidateChannel(Uri address, ChannelPoolKind poolKind = ChannelPoolKind.Unauthenticated);
    int EvictIdleChannels();
    int ActiveChannelCount { get; }
    int GetActiveChannelCount(ChannelPoolKind poolKind);
}

public sealed class PeerGrpcChannelFactory : IPeerGrpcChannelFactory
{
    private readonly PeerGrpcChannelFactoryOptions _options;
    private readonly IDateTimeProvider _timeProvider;
    private readonly Func<Uri, GrpcChannelOptions, GrpcChannel> _channelFactory;
    private readonly Dictionary<(Uri Address, ChannelPoolKind PoolKind), CachedChannelEntry> _channels = new();
    private readonly object _lock = new();
    private bool _disposed;

    private sealed class CachedChannelEntry
    {
        public required GrpcChannel Channel { get; init; }
        public required DateTimeOffset LastAccessedAtUtc { get; set; }
    }

    public PeerGrpcChannelFactory(
        PeerGrpcChannelFactoryOptions options,
        IDateTimeProvider timeProvider)
        : this(options, timeProvider, DefaultCreateChannel)
    {
    }

    internal PeerGrpcChannelFactory(
        PeerGrpcChannelFactoryOptions options,
        IDateTimeProvider timeProvider,
        Func<Uri, GrpcChannelOptions, GrpcChannel> channelFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
    }

    public int ActiveChannelCount
    {
        get
        {
            lock (_lock)
            {
                return _channels.Count;
            }
        }
    }

    public int GetActiveChannelCount(ChannelPoolKind poolKind)
    {
        lock (_lock)
        {
            return _channels.Keys.Count(k => k.PoolKind == poolKind);
        }
    }

    public GrpcChannel GetOrCreateChannel(Uri address, ChannelPoolKind poolKind = ChannelPoolKind.Unauthenticated)
    {
        ArgumentNullException.ThrowIfNull(address);

        var targetAddress = address.Scheme.Equals("percolator", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(address) { Scheme = Uri.UriSchemeHttps }.Uri
            : address;

        var key = (Address: targetAddress, PoolKind: poolKind);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var now = _timeProvider.UtcNow;
            EvictExpiredUnderLock(now);

            if (_channels.TryGetValue(key, out var existing))
            {
                existing.LastAccessedAtUtc = now;
                return existing.Channel;
            }

            if (_channels.Count >= _options.MaxCapacity)
            {
                EvictLruUnderLock();
            }

            var channelOptions = new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    ConnectTimeout = _options.ConnectTimeout,
                    EnableMultipleHttp2Connections = true,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = (_, _, _, _) => true
                    }
                },
                DisposeHttpClient = true
            };

            var channel = _channelFactory(targetAddress, channelOptions);
            _channels[key] = new CachedChannelEntry
            {
                Channel = channel,
                LastAccessedAtUtc = now
            };

            return channel;
        }
    }

    public void InvalidateChannel(Uri address, ChannelPoolKind poolKind = ChannelPoolKind.Unauthenticated)
    {
        ArgumentNullException.ThrowIfNull(address);

        var targetAddress = address.Scheme.Equals("percolator", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(address) { Scheme = Uri.UriSchemeHttps }.Uri
            : address;

        var key = (Address: targetAddress, PoolKind: poolKind);

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_channels.Remove(key, out var entry))
            {
                entry.Channel.Dispose();
            }
        }
    }

    public int EvictIdleChannels()
    {
        lock (_lock)
        {
            if (_disposed) return 0;
            return EvictExpiredUnderLock(_timeProvider.UtcNow);
        }
    }

    private int EvictExpiredUnderLock(DateTimeOffset now)
    {
        var expired = _channels
            .Where(kvp => (now - kvp.Value.LastAccessedAtUtc) >= _options.IdleTimeout)
            .ToList();

        foreach (var (key, entry) in expired)
        {
            _channels.Remove(key);
            entry.Channel.Dispose();
        }

        return expired.Count;
    }

    private void EvictLruUnderLock()
    {
        if (_channels.Count == 0) return;

        var oldest = _channels.OrderBy(kvp => kvp.Value.LastAccessedAtUtc).First();
        _channels.Remove(oldest.Key);
        oldest.Value.Channel.Dispose();
    }

    private static GrpcChannel DefaultCreateChannel(Uri address, GrpcChannelOptions options)
    {
        return GrpcChannel.ForAddress(address, options);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var entry in _channels.Values)
            {
                entry.Channel.Dispose();
            }

            _channels.Clear();
        }
    }
}
