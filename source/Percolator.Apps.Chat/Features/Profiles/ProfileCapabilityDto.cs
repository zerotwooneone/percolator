using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Profiles;

public sealed record ProfileCapabilityDto(
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    byte[] ProfileKeyBytes,
    byte[] BlindedLocatorBytes,
    byte[] MailboxRoutingTokenBytes, // Author's BlindedRoutingToken enabling the recipient to route sealed envelopes into author's mailbox
    uint Revision,
    ulong TimestampUnixMs);
