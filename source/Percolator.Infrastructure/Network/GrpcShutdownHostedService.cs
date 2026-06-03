using Microsoft.Extensions.Hosting;

namespace Percolator.Infrastructure.Network;

public class GrpcShutdownHostedService : IHostedService
{
    private readonly IGrpcServerManager _grpcServerManager;

    public GrpcShutdownHostedService(IGrpcServerManager grpcServerManager)
    {
        _grpcServerManager = grpcServerManager;
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken ct)
    {
        await _grpcServerManager.StopAsync(ct);
    }
}
