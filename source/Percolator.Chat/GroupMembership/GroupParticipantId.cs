using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.GroupMembership;

/// <summary>
/// Composite identity value object for group participants.
/// Models peers that may or may not have local identities.
/// The PKH is the authoritative global identifier (always present).
/// LocalPeerId is an optional local surrogate key for resolved peers.
/// </summary>
public sealed record GroupParticipantId(Pkh Pkh, ChatPeerId? LocalPeerId);
