using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Models;

public sealed record UnknownGroupMessageRecord(
    PayloadId Id,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    ReadOnlyMemory<byte> EncryptedPayload,
    ulong ReceivedAtUnixMs,
    ulong ExpiresAtUnixMs);
