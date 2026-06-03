using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Percolator.Infrastructure.Network;

public class PeerGrpcChannelFactory : IPeerGrpcChannelFactory, IAsyncDisposable
{
    private readonly ILogger<PeerGrpcChannelFactory> _logger;
    private readonly ConcurrentDictionary<string, Lazy<GrpcChannel>> _channelCache;

    public PeerGrpcChannelFactory(ILogger<PeerGrpcChannelFactory> logger)
    {
        _logger = logger;
        _channelCache = new ConcurrentDictionary<string, Lazy<GrpcChannel>>();
    }

    public GrpcChannel CreateChannel(DnsEndPoint endpoint)
    {
        // Key by physical URI string to prevent stale channel bug
        var uriString = $"https://{endpoint.Host}:{endpoint.Port}";

        return _channelCache.GetOrAdd(uriString, _ => new Lazy<GrpcChannel>(() =>
        {
            _logger.LogDebug("Creating new gRPC channel to {Uri}", uriString);

            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    TargetHost = endpoint.Host,
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true // Blind trust - security in X3DH payload
                },
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            return GrpcChannel.ForAddress(uriString, new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = 4 * 1024 * 1024,
                MaxSendMessageSize = 4 * 1024 * 1024
            });
        })).Value;
    }

    public async ValueTask DisposeAsync()
    {
        var shutdownTasks = new List<Task>();

        foreach (var lazyChannel in _channelCache.Values)
        {
            if (lazyChannel.IsValueCreated)
            {
                try
                {
                    // Fire off the graceful shutdown requests concurrently
                    shutdownTasks.Add(lazyChannel.Value.ShutdownAsync());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error initiating channel shutdown");
                }
            }
        }

        if (shutdownTasks.Any())
        {
            // Enforce a strict 2-second timeout on the graceful teardown
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
            var allShutdownsTask = Task.WhenAll(shutdownTasks);

            if (await Task.WhenAny(allShutdownsTask, timeoutTask) == timeoutTask)
            {
                _logger.LogWarning("Graceful shutdown of gRPC channels timed out. Forcing disposal.");
            }
        }

        // Always aggressively dispose to instantly free unmanaged OS handles,
        // terminating any stuck active calls.
        foreach (var lazyChannel in _channelCache.Values)
        {
            if (lazyChannel.IsValueCreated)
            {
                lazyChannel.Value.Dispose();
            }
        }

        _channelCache.Clear();
    }
}
