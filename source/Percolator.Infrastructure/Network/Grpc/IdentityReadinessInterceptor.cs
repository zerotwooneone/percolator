using Grpc.Core;
using Grpc.Core.Interceptors;
using Percolator.Application.Identity;

namespace Percolator.Infrastructure.Network.Grpc;

public sealed class IdentityReadinessInterceptor : Interceptor
{
    private readonly IActiveIdentityAccessor _activeIdentity;

    public IdentityReadinessInterceptor(IActiveIdentityAccessor activeIdentity)
    {
        _activeIdentity = activeIdentity ?? throw new ArgumentNullException(nameof(activeIdentity));
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        EnsureReady();
        return base.UnaryServerHandler(request, context, continuation);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureReady();
        return base.ClientStreamingServerHandler(requestStream, context, continuation);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureReady();
        return base.ServerStreamingServerHandler(request, responseStream, context, continuation);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureReady();
        return base.DuplexStreamingServerHandler(requestStream, responseStream, context, continuation);
    }

    private void EnsureReady()
    {
        if (_activeIdentity.IsActive)
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.Unavailable, "Node not ready (no active identity)."));
    }
}
