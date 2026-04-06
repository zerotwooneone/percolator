using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorStateInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
