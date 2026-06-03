using Grpc.Core;

namespace Percolator.Infrastructure.Network;

public interface IGrpcServiceActivator<T> where T : class
{
    GrpcActivatorHandle<T> Create(IServiceProvider serviceProvider);
    ValueTask ReleaseAsync(GrpcActivatorHandle<T> handle);
}

public class GrpcActivatorHandle<T> where T : class
{
    public T Service { get; }
    public bool Created { get; }
    public object? State { get; }

    public GrpcActivatorHandle(T service, bool created, object? state = null)
    {
        Service = service;
        Created = created;
        State = state;
    }
}
