using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.PluginSdk;

public sealed record TimelineCardDto(
    Guid CardId,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    DateTimeOffset TimestampUtc,
    AppId SourceAppId,
    string Title,
    string Summary,
    TimelineCardMetadata Metadata,
    IReadOnlyList<TimelineActionDto> Actions,
    byte[] CustomPayload);
