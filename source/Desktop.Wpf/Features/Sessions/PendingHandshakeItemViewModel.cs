using Percolator.Cryptography;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeItemViewModel : IDisposable
{
    private DisposableBag _bag;

    public string DisplayName { get; }
    public string Initials { get; set; }
    public string BundleText { get; set; } = "Not Set";
    public PendingSessionId PendingId { get; }

    // Reactive properties for UI updates
    public BindableReactiveProperty<string> StatusText { get; }
    public BindableReactiveProperty<bool> IsExpired { get; }

    public string? SendPath { get; set; }
    public string? RequestCorrelationId { get; set; }

    public bool IsRelayed { get; set; }
    public string? RelayInfoText { get; set; }

    public PendingHandshakeItemViewModel(string displayName, string initials, string bundleText, PendingSessionId pendingId, bool isRelayed, string? relayInfoText)
    {
        DisplayName = displayName;
        Initials = initials;
        BundleText = bundleText;
        PendingId = pendingId;
        IsRelayed = isRelayed;
        RelayInfoText = relayInfoText;

        StatusText = new BindableReactiveProperty<string>("Pending").AddTo(ref _bag);
        IsExpired = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
    }

    public void Dispose() => _bag.Dispose();
}
