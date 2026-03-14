using R3;

namespace Desktop.Wpf.Features.Sessions;

public interface ISecureChannelsListEvents
{
    Observable<Unit> Changed { get; }
    void NotifyChanged();
}

public sealed class SecureChannelsListEvents : ISecureChannelsListEvents
{
    private readonly Subject<Unit> _changed = new();

    public Observable<Unit> Changed => _changed;

    public void NotifyChanged() => _changed.OnNext(Unit.Default);
}
