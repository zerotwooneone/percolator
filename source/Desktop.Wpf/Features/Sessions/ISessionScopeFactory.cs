using Desktop.Wpf.Features.Chat;
using Percolator.Network;

namespace Desktop.Wpf.Features.Sessions
{
    public interface ISessionScopeFactory
    {
        SessionResolved GetOrCreate(DirectSessionId sessionId, SessionHeader? header = null);
    }

    public sealed class SessionResolved
    {
        public required SessionContext Context { get; init; }
        public required ChatViewModel ViewModel { get; init; }
    }

    public sealed class SessionHeader
    {
        public string? DisplayName { get; init; }
        public string? Initials { get; init; }
        public bool? IsOnline { get; init; }
    }
}
