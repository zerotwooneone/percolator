using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.DependencyInjection;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Vault;
using Percolator.Simulator.Features.Logging;
using R3;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatedPersonaHost : ISimulatedPersonaHost
{
    private readonly MasterKeyHandle _masterKeyHandle;
    private readonly ISimulatorLogSink _logSink;
    private readonly ReactiveProperty<bool> _isListening = new(true);
    private readonly string _nodeTempDirectory;
    private IHost? _host;
    private KestrelPeerTransportServer? _transportServer;
    private bool _disposed;

    public SimulatedNodeId Id { get; }
    public string DisplayName { get; }
    public int BoundPort { get; private set; }
    public Uri BoundEndpoint => new($"https://127.0.0.1:{BoundPort}");
    public byte[] IdenticonPng { get; }
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started.");
    public ReadOnlyReactiveProperty<bool> IsListening => _isListening;

    public SimulatedPersonaHost(
        SimulatedNodeId id,
        string displayName,
        byte[] identiconPng,
        int port,
        ISimulatorLogSink logSink)
    {
        Id = id;
        DisplayName = displayName;
        IdenticonPng = identiconPng;
        BoundPort = port;
        _logSink = logSink;
        _masterKeyHandle = MasterKeyHandle.GenerateRandom();

        _nodeTempDirectory = Path.Combine(Path.GetTempPath(), "PercolatorSim", $"node_{Id.Value:N}");
        Directory.CreateDirectory(_nodeTempDirectory);
    }

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        var storageOptions = new VaultStorageOptions
        {
            BaseDirectory = _nodeTempDirectory
        };

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((_, services) =>
        {
            // 1. Core infrastructure with random in-memory master key
            services.AddAuthenticatedInfrastructure(_masterKeyHandle, storageOptions);

            // 2. Node-scoped logger provider routing all Kestrel/Percolator logs to multi-sink
            services.AddLogging(lb =>
            {
                lb.ClearProviders();
                lb.SetMinimumLevel(LogLevel.Trace);
                lb.AddProvider(new NodeScopedLoggerProvider(DisplayName, Id, _logSink));
            });
        });

        _host = builder.Build();
        await _host.StartAsync(ct);

        _transportServer = _host.Services.GetRequiredService<KestrelPeerTransportServer>();
        await _transportServer.StartAsync(new PortNumber(BoundPort), ct);
        if (_transportServer.BoundPort.HasValue)
        {
            BoundPort = _transportServer.BoundPort.Value.Value;
        }

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "HostLifecycle",
            $"Persona host started and listening on https://127.0.0.1:{BoundPort}"));
    }

    public void SetListening(bool isListening)
    {
        _isListening.Value = isListening;
        if (isListening)
        {
            if (_transportServer is not null && !_transportServer.IsRunning)
            {
                _transportServer.StartAsync(new PortNumber(BoundPort)).GetAwaiter().GetResult();
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    DisplayName,
                    Id,
                    "HostLifecycle",
                    "Ingress resumed (Listening)."));
            }
        }
        else
        {
            if (_transportServer is not null && _transportServer.IsRunning)
            {
                _transportServer.StopAsync().GetAwaiter().GetResult();
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Information,
                    DisplayName,
                    Id,
                    "HostLifecycle",
                    "Ingress muted (Transport stopped)."));
            }
        }
    }

    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_transportServer is not null)
        {
            await _transportServer.StopAsync(ct);
        }

        if (_host is not null)
        {
            await _host.StopAsync(ct);
        }

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "HostLifecycle",
            "Persona host stopped."));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();

        _host?.Dispose();
        _masterKeyHandle.Dispose();
        _isListening.Dispose();

        try
        {
            if (Directory.Exists(_nodeTempDirectory))
            {
                Directory.Delete(_nodeTempDirectory, recursive: true);
            }
        }
        catch { }
    }
}
