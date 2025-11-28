using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Sessions;

public sealed class InMemorySessionDirectory : ISessionDirectory
{
    private static readonly SessionListItem[] Seed = CreateSeed();

    private static SessionListItem[] CreateSeed()
    {
        static string MakeInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                var first = parts[0];
                return first.Substring(0, Math.Min(2, first.Length)).ToUpperInvariant();
            }
            return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
        }

        var s1 = new SessionListItem { Id = "1" };
        s1.DisplayName.Value = "Sarah Connor";
        s1.Initials.Value = MakeInitials("Sarah Connor");
        s1.LastMessagePreview.Value = "The encryption keys have b...";
        s1.TimestampText.Value = "10:42 AM";
        s1.UnreadCount.Value = 2;
        s1.IsOnline.Value = true;

        var s2 = new SessionListItem { Id = "2" };
        s2.DisplayName.Value = "Neo Anderson";
        s2.Initials.Value = MakeInitials("Neo Anderson");
        s2.LastMessagePreview.Value = "Follow the white rabbit.";
        s2.TimestampText.Value = "Yesterday";
        s2.UnreadCount.Value = 0;
        s2.IsOnline.Value = false;

        var s3 = new SessionListItem { Id = "3" };
        s3.DisplayName.Value = "Trinity";
        s3.Initials.Value = MakeInitials("Trinity");
        s3.LastMessagePreview.Value = "Secure line established.";
        s3.TimestampText.Value = "Yesterday";
        s3.UnreadCount.Value = 0;
        s3.IsOnline.Value = true;

        var s4 = new SessionListItem { Id = "4" };
        s4.DisplayName.Value = "Morpheus";
        s4.Initials.Value = MakeInitials("Morpheus");
        s4.LastMessagePreview.Value = "I can only show you the door.";
        s4.TimestampText.Value = "Mon";
        s4.UnreadCount.Value = 0;
        s4.IsOnline.Value = false;

        return new[] { s1, s2, s3, s4 };
    }

    public Task<IReadOnlyList<SessionListItem>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SessionListItem>>(Seed);
}
