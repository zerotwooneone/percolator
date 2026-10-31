namespace Percolator.Infrastructure.Transport;

using System;
using System.Threading.Tasks;
using Grpc.AspNetCore.Server;
using Microsoft.Extensions.DependencyInjection;

public sealed class PrimaryContainerServiceActivator<T> : IGrpcServiceActivator<T> where T : class
{
    private readonly IServiceProvider _primaryContainer;

    public PrimaryContainerServiceActivator(IServiceProvider primaryContainer)
    {
        _primaryContainer = primaryContainer ?? throw new ArgumentNullException(nameof(primaryContainer));
    }

    public GrpcActivatorHandle<T> Create(IServiceProvider serviceProvider)
    {
        var scope = _primaryContainer.CreateScope();
        try
        {
            var service = ActivatorUtilities.GetServiceOrCreateInstance<T>(scope.ServiceProvider);
            return new GrpcActivatorHandle<T>(service, created: true, state: scope);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public async ValueTask ReleaseAsync(GrpcActivatorHandle<T> action)
    {
        if (action.State is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (action.State is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
