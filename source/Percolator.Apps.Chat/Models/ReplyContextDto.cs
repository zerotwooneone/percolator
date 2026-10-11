using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models;

public sealed record ReplyContextDto(
    PayloadId ParentPayloadId,
    PublicIdentityId ParentAuthorId);
