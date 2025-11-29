using System;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Wpf.Features.Sessions
{
    public interface ISessionScopeFactory
    {
        SessionResolved GetOrCreate(string sessionId, SessionHeader? header = null);
    }

    public sealed class SessionResolved
    {
        public required SessionContext Context { get; init; }
        public required object View { get; init; }
    }

    public sealed class SessionHeader
    {
        public string? DisplayName { get; init; }
        public string? Initials { get; init; }
        public bool? IsOnline { get; init; }
    }
}
