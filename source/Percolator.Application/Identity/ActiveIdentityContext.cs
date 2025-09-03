using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Chat;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;

namespace Percolator.Application.Identity;

/// <summary>
/// Holds the details of the currently active identity for the running node.
/// This context is populated at startup and treated as read-only thereafter.
/// </summary>
public class ActiveIdentityContext :ISelfParticipantIdProvider
{
    public IdentityRecord? Identity { get; internal set; }
    public X3dhKeys? Keys { get; internal set; }

    ChatParticipantId ISelfParticipantIdProvider.Get()
    {
        if (Identity is null)
        {
            throw new InvalidOperationException("Identity not loaded for chat participant.");
        }
        return new ChatParticipantId(Identity.Id);
    }
}
