using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Shared.Mvvm;
using Microsoft.Extensions.DependencyInjection;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PeerConnectionReloadCoordinator : IDisposable
{
    private readonly Subject<Unit> _reloadTrigger = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PeerConnectionStateService _state;
    private DisposableBag _bag;

    public PeerConnectionReloadCoordinator(
        IServiceScopeFactory scopeFactory, 
        PeerConnectionStateService state,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _state = state;

        _reloadTrigger
            .Debounce(TimeSpan.FromMilliseconds(250),timeProvider)
            .SubscribeAwait(async (_, ct) => await ReloadCoreAsync(ct).ConfigureAwait(false), AwaitOperation.Drop)
            .AddTo(ref _bag);
    }

    public void TriggerReload() => _reloadTrigger.OnNext(Unit.Default);

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        if (!_state.ActiveSelfIdentityId.HasValue) return;

        using var scope = _scopeFactory.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<IPeerConnectionQueries>();

        var connections = await queries.LoadAllConnectionsAsync(_state.ActiveSelfIdentityId.Value.Value, cancellationToken).ConfigureAwait(false);
        _state.UpdateConnections(connections);

        var pending = await queries.LoadPendingInboundAsync(cancellationToken).ConfigureAwait(false);
        _state.UpdatePendingInbound(pending);
    }

    public void Dispose()
    {
        _bag.Dispose();
        _reloadTrigger.Dispose();
    }
}
