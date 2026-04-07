using Desktop.Wpf.Features.Simulator.Models;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateRepository
{
    Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken cancellationToken = default);

    Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken cancellationToken = default);
}
