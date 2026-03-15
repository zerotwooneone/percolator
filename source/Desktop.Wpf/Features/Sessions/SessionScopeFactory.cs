using Microsoft.Extensions.DependencyInjection;
using Desktop.Wpf.Features.Chat;

namespace Desktop.Wpf.Features.Sessions
{
    public sealed class SessionScopeFactory : ISessionScopeFactory, IDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly Dictionary<string, IServiceScope> _scopes = new();
        private readonly LinkedList<string> _lru = new();
        private const int ScopeCapacity = 3;

        public SessionScopeFactory(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public SessionResolved GetOrCreate(string sessionId, SessionHeader? header = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));

            if (!_scopes.TryGetValue(sessionId, out var scope))
            {
                scope = _scopeFactory.CreateScope();
                _scopes[sessionId] = scope;
                _lru.AddFirst(sessionId);
                if (_lru.Count > ScopeCapacity)
                {
                    var toEvict = _lru.Last!.Value;
                    _lru.RemoveLast();
                    if (_scopes.Remove(toEvict, out var evicted))
                    {
                        try { evicted.Dispose(); } catch { }
                    }
                }
            }
            else
            {
                var node = _lru.Find(sessionId);
                if (node is not null)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                }
            }

            var ctx = scope.ServiceProvider.GetRequiredService<SessionContext>();
            ctx.SetSessionId(sessionId);
            if (header is not null)
            {
                if (header.DisplayName is not null) ctx.PeerName.Value = header.DisplayName;
                if (header.Initials is not null) ctx.Initials.Value = header.Initials;
                if (header.IsOnline.HasValue) ctx.IsOnline.Value = header.IsOnline.Value;
            }

            var chatVm = scope.ServiceProvider.GetRequiredService<ChatViewModel>();
            chatVm.SetSession(sessionId);

            return new SessionResolved
            {
                Context = ctx,
                ViewModel = chatVm
            };
        }

        public void Dispose()
        {
            foreach (var kv in _scopes.Values)
            {
                try { kv.Dispose(); } catch { }
            }
            _scopes.Clear();
            _lru.Clear();
        }
    }
}
