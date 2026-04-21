using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionContext
{
    private const string UninitializedId = "uninitialized";
    public string SessionId { get; private set; } = UninitializedId;
    public ReactiveProperty<string> PeerName { get; } = new("");
    public ReactiveProperty<string> Initials { get; } = new("?");
    public ReactiveProperty<bool> IsOnline { get; } = new(false);
    // Per-session composer draft text
    public ReactiveProperty<string> Draft { get; } = new("");

    public void SetSessionId(string id)
    {
        if (SessionId != UninitializedId && id != SessionId)
        {
            throw new InvalidOperationException("cannot change session id");
        }
        SessionId = id;
    }
}
