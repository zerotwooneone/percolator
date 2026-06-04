using System.Net.Http;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorGrpcClientFactory : ISimulatorGrpcClientFactory, IDisposable
{
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly ILogger<SimulatorGrpcClientFactory> _logger;
    private GrpcChannel? _cachedChannel;
    private int? _cachedPort;
    private HttpClientHandler? _httpHandler;

    public SimulatorGrpcClientFactory(
        ActiveIdentityContext activeIdentityContext,
        ILogger<SimulatorGrpcClientFactory> logger)
    {
        _activeIdentityContext = activeIdentityContext;
        _logger = logger;
    }

    public TransportService.TransportServiceClient CreateClient()
    {
        if (_activeIdentityContext.Identity is null)
        {
            throw new InvalidOperationException("Cannot bridge simulator to main app: Main app identity is not active.");
        }

        var currentPort = _activeIdentityContext.Identity.ListeningPort.Value;

        // Recreate channel only if port has changed
        if (_cachedChannel is null || _cachedPort != currentPort)
        {
            DisposeCachedResources();

            _cachedPort = currentPort;
            var address = $"https://127.0.0.1:{currentPort}";

            // The localhost Kestrel server uses a self-signed ephemeral cert, so the simulator client must blind-trust it.
            _httpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            var channelOptions = new GrpcChannelOptions { HttpHandler = _httpHandler };
            _cachedChannel = GrpcChannel.ForAddress(address, channelOptions);

            _logger.LogInformation("Created new gRPC channel for simulator to main app on port {Port}", currentPort);
        }

        return new TransportService.TransportServiceClient(_cachedChannel);
    }

    private void DisposeCachedResources()
    {
        _cachedChannel?.Dispose();
        _cachedChannel = null;
        _httpHandler?.Dispose();
        _httpHandler = null;
    }

    public void Dispose()
    {
        DisposeCachedResources();
    }
}
