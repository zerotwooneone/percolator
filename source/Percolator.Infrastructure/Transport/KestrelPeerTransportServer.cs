namespace Percolator.Infrastructure.Transport;

using System;
using System.Net;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Infrastructure.Transport.Endpoints;

public sealed class KestrelPeerTransportServer : IPeerTransportServer, IHostedService
{
    private readonly ITransportCertificateProvider _certificateProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly PeerTransportServerOptions _options;
    private readonly ILogger<KestrelPeerTransportServer>? _logger;
    private readonly object _lock = new();

    private WebApplication? _app;
    private bool _isRunning;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _isRunning;
            }
        }
    }

    public PortNumber? BoundPort { get; private set; }
    public IPAddress? BoundAddress { get; private set; }

    public KestrelPeerTransportServer(
        ITransportCertificateProvider certificateProvider,
        IServiceProvider serviceProvider,
        PeerTransportServerOptions options,
        ILogger<KestrelPeerTransportServer>? logger = null)
    {
        _certificateProvider = certificateProvider ?? throw new ArgumentNullException(nameof(certificateProvider));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task StartAsync(
        IPAddress? bindAddress = null,
        PortNumber? preferredPort = null,
        CancellationToken ct = default)
    {
        var targetBindAddress = bindAddress ?? _options.BindAddress;
        ArgumentNullException.ThrowIfNull(targetBindAddress);

        var cert = await _certificateProvider.GetOrCreateServerCertificateAsync(ct).ConfigureAwait(false);

        WebApplication app;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRunning)
            {
                return;
            }

            var requestedPort = preferredPort?.Value ?? 0;
            var builder = WebApplication.CreateBuilder();

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(targetBindAddress, requestedPort, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http2;
                    listenOptions.UseHttps(cert, httpsOptions =>
                    {
                        httpsOptions.SslProtocols = SslProtocols.Tls13;
                    });
                });
            });

            builder.Services.AddGrpc();

            // Bridge endpoint activation to the parent Stage 2 DI container per request
            builder.Services.AddSingleton<IGrpcServiceActivator<UnauthenticatedDeliveryEndpoint>>(
                new ParentContainerServiceActivator<UnauthenticatedDeliveryEndpoint>(_serviceProvider));
            builder.Services.AddSingleton<IGrpcServiceActivator<AuthenticatedRelayEndpoint>>(
                new ParentContainerServiceActivator<AuthenticatedRelayEndpoint>(_serviceProvider));
            builder.Services.AddSingleton<IGrpcServiceActivator<AnonymousGroupRelayEndpoint>>(
                new ParentContainerServiceActivator<AnonymousGroupRelayEndpoint>(_serviceProvider));

            app = builder.Build();
            app.MapGrpcService<UnauthenticatedDeliveryEndpoint>();
            app.MapGrpcService<AuthenticatedRelayEndpoint>();
            app.MapGrpcService<AnonymousGroupRelayEndpoint>();

            _app = app;
        }

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
            {
                _app = null;
                _isRunning = false;
                BoundPort = null;
                BoundAddress = null;
            }
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        lock (_lock)
        {
            _isRunning = true;
            BoundAddress = targetBindAddress;

            var server = app.Services.GetService<IServer>();
            var addressFeature = server?.Features.Get<IServerAddressesFeature>();
            if (addressFeature != null)
            {
                foreach (var address in addressFeature.Addresses)
                {
                    if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                    {
                        BoundPort = new PortNumber(uri.Port);
                        if (IPAddress.TryParse(uri.Host, out var parsedIp))
                        {
                            BoundAddress = parsedIp;
                        }
                        break;
                    }
                }
            }
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        WebApplication? appToStop;
        lock (_lock)
        {
            if (!_isRunning || _app == null)
            {
                return;
            }

            appToStop = _app;
            _app = null;
            _isRunning = false;
            BoundPort = null;
            BoundAddress = null;
        }

        try
        {
            await appToStop.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await appToStop.DisposeAsync().ConfigureAwait(false);
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        return StartAsync(_options.BindAddress, ct: cancellationToken);
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        return StopAsync(ct: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }

    private sealed class ParentContainerServiceActivator<TService> : IGrpcServiceActivator<TService> where TService : class
    {
        private readonly IServiceProvider _parentProvider;

        public ParentContainerServiceActivator(IServiceProvider parentProvider)
        {
            _parentProvider = parentProvider;
        }

        public GrpcActivatorHandle<TService> Create(IServiceProvider serviceProvider)
        {
            var scope = _parentProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<TService>();
            return new GrpcActivatorHandle<TService>(service, true, scope);
        }

        public ValueTask ReleaseAsync(GrpcActivatorHandle<TService> handle)
        {
            if (handle.State is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }

            if (handle.State is IDisposable disposable)
            {
                disposable.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
