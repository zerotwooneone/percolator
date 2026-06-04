using Percolator.Contracts;

namespace Desktop.Wpf.Features.Simulator;

public interface ISimulatorGrpcClientFactory
{
    TransportService.TransportServiceClient CreateClient();
}
