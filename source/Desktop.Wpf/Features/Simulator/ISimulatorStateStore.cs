using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateStore
{
    Task<SimulatorStateDto?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SimulatorStateDto state, CancellationToken cancellationToken = default);
}
