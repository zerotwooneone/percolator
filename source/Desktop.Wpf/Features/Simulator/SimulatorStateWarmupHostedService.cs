using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorStateWarmupHostedService : IHostedService
{
    private readonly ILogger<SimulatorStateWarmupHostedService> _logger;
    private readonly ISimulatorStateInitializer _initializer;

    public SimulatorStateWarmupHostedService(
        ILogger<SimulatorStateWarmupHostedService> logger,
        ISimulatorStateInitializer initializer)
    {
        _logger = logger;
        _initializer = initializer;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[simulator] Initializing simulator state");
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("[simulator] Simulator state initialized");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
