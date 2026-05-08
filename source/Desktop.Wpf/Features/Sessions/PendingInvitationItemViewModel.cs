using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Shared.Mvvm;
using Percolator.Cryptography;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingInvitationItemViewModel : IDisposable
{
    private readonly DisposableBag _bag;
    private readonly PeerPendingInvitationModel _model;

    public PendingSessionId PendingSessionId => new PendingSessionId(_model.PendingSessionId);
    public BindableReactiveProperty<string> DisplayName { get; }
    public BindableReactiveProperty<string> Initials { get; }
    public BindableReactiveProperty<bool> IsRelayed { get; }
    public BindableReactiveProperty<string?> RelayInfoText { get; }
    
    // Reactive properties for UI updates
    public BindableReactiveProperty<string> StatusText { get; }
    public BindableReactiveProperty<bool> IsExpired { get; }

    public string? SendPath { get; set; }
    public string? RequestCorrelationId { get; set; }

    public PendingInvitationItemViewModel(PeerPendingInvitationModel model, IUiDispatcher ui)
    {
        _model = model;

        // Derived reactively from the model
        DisplayName = model.PeerName
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty("")
            .AddTo(ref _bag);

        Initials = model.PeerName
            .DistinctUntilChanged()
            .Select(name => ComputeInitials(name))
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty("")
            .AddTo(ref _bag);

        IsRelayed = model.IsRelayed
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);

        RelayInfoText = Observable.CombineLatest(
                model.IsRelayed,
                model.RelayPeerName,
                model.RelayEndpoint,
                (isRelayed, relayPeerName, relayEndpoint) =>
                    isRelayed
                        ? $"Via relay: {relayPeerName}{(string.IsNullOrWhiteSpace(relayEndpoint) ? "" : $" ({relayEndpoint})")}"
                        : null)
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty((string?)null)
            .AddTo(ref _bag);

        StatusText = new BindableReactiveProperty<string>("Pending").AddTo(ref _bag);
        IsExpired = model.ExpiresAtUtc
            .Select(expires => expires.HasValue && expires.Value < DateTimeOffset.UtcNow)
            .DistinctUntilChanged()
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(false)
            .AddTo(ref _bag);
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }

    public void Dispose() => _bag.Dispose();
}
