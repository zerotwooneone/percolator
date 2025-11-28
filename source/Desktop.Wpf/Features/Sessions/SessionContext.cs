using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class SessionContext
{
    private const string UninitializedId = "uninitialized";
    public string SessionId { get; private set; } = UninitializedId;
    public BindableReactiveProperty<string> PeerName { get; } = new("");
    public BindableReactiveProperty<string> Initials { get; } = new("?");
    public BindableReactiveProperty<bool> IsOnline { get; } = new(false);
    // Per-session composer draft text
    public BindableReactiveProperty<string> Draft { get; } = new("");

    public void SetSessionId(string id)
    {
        if (SessionId != UninitializedId && id != SessionId)
        {
            throw new InvalidOperationException("cannot change session id");
        }
        SessionId = id;
    }
}
