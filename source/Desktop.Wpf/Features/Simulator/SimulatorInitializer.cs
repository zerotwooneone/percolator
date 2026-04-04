using System.Collections.ObjectModel;
using System.Windows;
using ObservableCollections;
using R3;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorInitializer
{
    Task InitializeAsync(CancellationToken ct = default);

}

public sealed class SimulatorInitializer : ISimulatorInitializer
{
    private readonly ISimulatorStateService _state;

    private readonly object _initGate = new();
    private Task? _initializeTask;

    public SimulatorInitializer(ISimulatorStateService state)
    {
        _state = state;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Task? inFlight;
        lock (_initGate)
        {
            inFlight = _initializeTask;
            if (inFlight is null || inFlight.IsCompleted)
            {
                _initializeTask = InitializeCoreAsync(ct);
                inFlight = _initializeTask;
            }
        }

        await inFlight.ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        try
        {
            await _state.InitializeAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_initGate)
            {
                _initializeTask = null;
            }
        }
    }
}
