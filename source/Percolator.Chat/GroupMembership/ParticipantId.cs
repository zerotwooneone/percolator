using Percolator.Chat.GroupLedger;

namespace Percolator.Chat.GroupMembership;

/// <summary>
/// Polymorphic participant identifier for group conversations.
/// Guarantees presence of PublicIdentityId and either a ChatPeerId (remote peer) or ChatSelfId (local self).
/// </summary>
public abstract record ParticipantId
{
    /// <summary>
    /// Universal public identity identifier (UUID), stable across devices and sessions.
    /// </summary>
    public PublicIdentityId PublicIdentityId { get; }

    protected ParticipantId(PublicIdentityId publicIdentityId)
    {
        PublicIdentityId = publicIdentityId;
    }
}

/// <summary>
/// Remote participant identifier with PublicIdentityId and ChatPeerId.
/// </summary>
public sealed record RemoteParticipantId(PublicIdentityId PublicIdentityId, ChatPeerId PeerId) : ParticipantId(PublicIdentityId);

/// <summary>
/// Local participant identifier with PublicIdentityId and ChatSelfId.
/// </summary>
public sealed record LocalParticipantId(PublicIdentityId PublicIdentityId, ChatSelfId SelfId) : ParticipantId(PublicIdentityId);
