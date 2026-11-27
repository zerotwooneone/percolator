using Microsoft.Extensions.DependencyInjection;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Features.Runtime;

/// <summary>
/// Singleton dispatcher that creates a fresh DI scope per background outbound payload so scoped
/// IPayloadSender / DbContext instances are never captured across threads or after scope disposal.
/// </summary>
public sealed class ScopedBackgroundPayloadDispatcher : IBackgroundPayloadDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ScopedBackgroundPayloadDispatcher(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async ValueTask<DomainResult> DispatchAsync(OutboundPayloadContext context, CancellationToken ct = default)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IPayloadSender sender = scope.ServiceProvider.GetRequiredService<IPayloadSender>();
        return await sender.SendPayloadAsync(context, ct).ConfigureAwait(false);
    }
}
