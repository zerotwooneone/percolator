using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Profiles;

public sealed record ShareProfileCapabilityCommand(
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    PublicIdentityId RecipientIdentityId,
    DeviceId RecipientDeviceId,
    ProfileKey ProfileKey,
    BlindedProfileLocator BlindedLocator,
    BlindedRoutingToken MailboxRoutingToken,
    uint Revision,
    DeliveryRoute Route,
    IdentityKey? AuthorPublicKey = null,
    string? SourceEndpoint = null,
    ProfileSourceType? SourceType = null);
