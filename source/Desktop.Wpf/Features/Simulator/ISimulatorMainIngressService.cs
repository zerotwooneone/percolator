using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorMainIngressService
{
    Task SendEstablishDirectSessionToMainAsync(EstablishDirectSessionRequest request, CancellationToken cancellationToken = default);
}
