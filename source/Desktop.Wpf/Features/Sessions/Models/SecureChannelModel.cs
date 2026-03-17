using R3;

namespace Desktop.Wpf.Features.Sessions.Models;

public abstract record ChannelRoute
{
    private ChannelRoute() { }

    public sealed record Direct : ChannelRoute;

    public sealed record Relayed(Guid? RelayHostPeerId) : ChannelRoute;

    public static ChannelRoute DirectRoute { get; } = new Direct();
}

public enum SecureChannelKind
{
    Direct,
    Relay,
    Group,
    PendingInbound,
    PendingOutbound,
    Failed
}

public sealed class SecureChannelModel : IDisposable
{
    private DisposableBag _bag;

    private readonly ReactiveProperty<string> _displayName;
    private readonly ReactiveProperty<string> _initials;
    private readonly ReactiveProperty<SecureChannelKind> _kind;
    private readonly ReactiveProperty<string?> _lastSnippet;
    private readonly ReactiveProperty<int> _unreadCount;
    private readonly ReactiveProperty<bool> _isOnline;
    private readonly ReactiveProperty<ChannelRoute> _route;
    private readonly ReactiveProperty<DateTimeOffset> _lastUpdate;

    public SecureChannelModel(
        SecureChannelKey key,
        string displayName,
        string initials,
        SecureChannelKind kind,
        DateTimeOffset lastUpdateUtc,
        ChannelRoute? route = null)
    {
        Key = key;

        _displayName = new ReactiveProperty<string>(displayName);
        _initials = new ReactiveProperty<string>(initials);
        _kind = new ReactiveProperty<SecureChannelKind>(kind);
        _lastSnippet = new ReactiveProperty<string?>(null);
        _unreadCount = new ReactiveProperty<int>(0);
        _isOnline = new ReactiveProperty<bool>(false);
        _route = new ReactiveProperty<ChannelRoute>(route ?? ChannelRoute.DirectRoute);
        _lastUpdate = new ReactiveProperty<DateTimeOffset>(lastUpdateUtc);
    }

    public SecureChannelKey Key { get; internal set; }

    public ReadOnlyReactiveProperty<string> DisplayName => _displayName;
    public ReadOnlyReactiveProperty<string> Initials => _initials;
    public ReadOnlyReactiveProperty<SecureChannelKind> Kind => _kind;
    public ReadOnlyReactiveProperty<string?> LastSnippet => _lastSnippet;
    public ReadOnlyReactiveProperty<int> UnreadCount => _unreadCount;
    public ReadOnlyReactiveProperty<bool> IsOnline => _isOnline;
    public ReadOnlyReactiveProperty<ChannelRoute> Route => _route;
    public ReadOnlyReactiveProperty<DateTimeOffset> LastUpdateUtc => _lastUpdate;

    internal string DisplayNameCurrent => _displayName.Value;
    internal string InitialsCurrent => _initials.Value;
    internal SecureChannelKind KindCurrent => _kind.Value;
    internal string? LastSnippetCurrent => _lastSnippet.Value;
    internal int UnreadCountCurrent => _unreadCount.Value;
    internal bool IsOnlineCurrent => _isOnline.Value;
    internal ChannelRoute RouteCurrent => _route.Value;
    internal DateTimeOffset LastUpdateUtcCurrent => _lastUpdate.Value;

    internal void SetDisplayName(string displayName) => _displayName.Value = displayName;
    internal void SetInitials(string initials) => _initials.Value = initials;
    internal void SetKind(SecureChannelKind kind) => _kind.Value = kind;
    internal void SetLastSnippet(string? snippet) => _lastSnippet.Value = snippet;
    internal void SetUnreadCount(int unreadCount) => _unreadCount.Value = unreadCount;
    internal void SetOnline(bool isOnline) => _isOnline.Value = isOnline;
    internal void SetRoute(ChannelRoute route) => _route.Value = route;
    internal void SetLastUpdateUtc(DateTimeOffset lastUpdateUtc) => _lastUpdate.Value = lastUpdateUtc;

    public void Dispose() => _bag.Dispose();
}
