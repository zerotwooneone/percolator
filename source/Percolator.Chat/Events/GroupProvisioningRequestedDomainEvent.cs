using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.SeedWork;

namespace Percolator.Chat.Events;

/// <summary>
/// Domain event raised when a group is created and needs to be provisioned on the relay.
/// </summary>
public sealed record GroupProvisioningRequestedDomainEvent(
    Messaging.ValueObjects.ConversationId ConversationId,
    RelayGroupPublicParamsBytes PublicParams,
    IReadOnlyList<PublicIdentityId> MemberPublicIdentityIds) : IDomainEvent;
