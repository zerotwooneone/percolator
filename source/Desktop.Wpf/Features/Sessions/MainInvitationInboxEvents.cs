using R3;

namespace Desktop.Wpf.Features.Sessions;

public interface IMainInvitationInboxEvents
{
    Observable<Unit> Changed { get; }
    void NotifyChanged();
}

public sealed class MainInvitationInboxEvents : IMainInvitationInboxEvents
{
    private readonly Subject<Unit> _changed = new();

    public Observable<Unit> Changed => _changed;

    public void NotifyChanged() => _changed.OnNext(Unit.Default);
}
