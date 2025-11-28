using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Sessions;

public sealed class InMemorySessionDirectory : ISessionDirectory
{
    private static readonly SessionListItem[] Seed = new[]
    {
        new SessionListItem { Id = "1", DisplayName = "Sarah Connor", LastMessagePreview = "The encryption keys have b...", TimestampText = "10:42 AM", UnreadCount = 2, IsOnline = true },
        new SessionListItem { Id = "2", DisplayName = "Neo Anderson", LastMessagePreview = "Follow the white rabbit.", TimestampText = "Yesterday", UnreadCount = 0, IsOnline = false },
        new SessionListItem { Id = "3", DisplayName = "Trinity", LastMessagePreview = "Secure line established.", TimestampText = "Yesterday", UnreadCount = 0, IsOnline = true },
        new SessionListItem { Id = "4", DisplayName = "Morpheus", LastMessagePreview = "I can only show you the door.", TimestampText = "Mon", UnreadCount = 0, IsOnline = false },
    };

    public Task<IReadOnlyList<SessionListItem>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SessionListItem>>(Seed);
}
