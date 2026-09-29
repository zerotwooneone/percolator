using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application2.Ingress;

public interface IAppRouter
{
    void RegisterHandler(IAppPayloadHandler handler);
    DomainResult<IAppPayloadHandler> Resolve(AppId appId);
}

public sealed class AppRouter : IAppRouter
{
    private readonly IAppPayloadHandler?[] _handlers = new IAppPayloadHandler?[256];

    public void RegisterHandler(IAppPayloadHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.TargetAppId.Value] = handler;
    }

    public DomainResult<IAppPayloadHandler> Resolve(AppId appId)
    {
        var handler = _handlers[appId.Value];
        if (handler == null)
        {
            return DomainResult<IAppPayloadHandler>.Failure(new DomainError(
                "UNKNOWN_APP_ID", $"No application payload handler registered for AppId 0x{appId.Value:X2}."));
        }

        return DomainResult<IAppPayloadHandler>.Success(handler);
    }
}
