using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Infrastructure.Network;

public class PrimaryContainerServiceActivator<T> : IGrpcServiceActivator<T> where T : class
{
    private readonly IServiceProvider _primaryProvider;

    public PrimaryContainerServiceActivator(IServiceProvider primaryProvider)
    {
        _primaryProvider = primaryProvider;
    }

    public GrpcActivatorHandle<T> Create(IServiceProvider serviceProvider)
    {
        // Create a dedicated scope for this specific gRPC request
        var scope = _primaryProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<T>();

        // Pass the scope as state so it can be disposed later
        return new GrpcActivatorHandle<T>(service, created: true, state: scope);
    }

    public ValueTask ReleaseAsync(GrpcActivatorHandle<T> handle)
    {
        // Dispose the scope when the gRPC request completes
        if (handle.State is IDisposable scope)
        {
            scope.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
