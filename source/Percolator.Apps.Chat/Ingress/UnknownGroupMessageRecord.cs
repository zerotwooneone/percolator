using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Ingress;

public sealed record UnknownGroupMessageRecord(
    PayloadId Id,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    ReadOnlyMemory<byte> EncryptedPayload,
    ulong ReceivedAtUnixMs,
    ulong ExpiresAtUnixMs);
