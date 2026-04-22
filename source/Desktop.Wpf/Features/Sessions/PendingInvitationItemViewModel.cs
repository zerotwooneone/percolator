using Percolator.Cryptography;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingInvitationItemViewModel : IDisposable
{
    private DisposableBag _bag;

    public PendingSessionId PendingSessionId { get; }
    public string DisplayName { get; }
    public string Initials { get; }
    public bool IsRelayed { get; }
    public string? RelayInfoText { get; }
    
    // Reactive properties for UI updates
    public BindableReactiveProperty<string> StatusText { get; }
    public BindableReactiveProperty<bool> IsExpired { get; }

    public string? SendPath { get; set; }
    public string? RequestCorrelationId { get; set; }

    public PendingInvitationItemViewModel(PendingSessionId pendingSessionId, string displayName, string initials, bool isRelayed, string? relayInfoText)
    {
        PendingSessionId = pendingSessionId;
        DisplayName = displayName;
        Initials = initials;
        IsRelayed = isRelayed;
        RelayInfoText = relayInfoText;

        StatusText = new BindableReactiveProperty<string>("Pending").AddTo(ref _bag);
        IsExpired = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
    }

    public void Dispose() => _bag.Dispose();
}
