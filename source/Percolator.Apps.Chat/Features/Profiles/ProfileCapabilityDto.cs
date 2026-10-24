using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Features.Profiles;

public sealed record ProfileCapabilityDto(
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    byte[] ProfileKeyBytes,
    byte[] BlindedLocatorBytes,
    byte[] MailboxRoutingTokenBytes, // Author's BlindedRoutingToken enabling the recipient to route sealed envelopes into author's mailbox
    uint Revision,
    ulong TimestampUnixMs,
    byte[]? AuthorPublicKeyBytes = null,
    string? SourceEndpoint = null,
    ProfileSourceType SourceType = ProfileSourceType.DirectP2P);
