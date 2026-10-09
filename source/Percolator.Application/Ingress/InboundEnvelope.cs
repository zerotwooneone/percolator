using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Ingress;

public abstract record InboundEnvelope(
    ChannelId ChannelId,
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    DateTimeOffset ReceivedAtUtc);

public sealed record InboundDirectEnvelope(
    ChannelId ChannelId,
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    RatchetHeader Header,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Ciphertext,
    DateTimeOffset ReceivedAtUtc)
    : InboundEnvelope(ChannelId, RecipientIdentityId, SenderIdentityId, SenderDeviceId, ReceivedAtUtc);

public sealed record InboundGroupEnvelope(
    ChannelId ChannelId,
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId AuthorIdentityId,
    DeviceId AuthorDeviceId,
    uint Iteration,
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> Signature,
    DateTimeOffset ReceivedAtUtc)
    : InboundEnvelope(ChannelId, RecipientIdentityId, AuthorIdentityId, AuthorDeviceId, ReceivedAtUtc);

public sealed record InboundHandshakeEnvelope(
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    IdentityKey SenderIdentityKey,
    DhPublicKey SenderEphemeralKey,
    uint SignedPreKeyId,
    uint? OneTimePreKeyId,
    ReadOnlyMemory<byte> EncryptedPayload,
    DateTimeOffset ReceivedAtUtc)
    : InboundEnvelope(new ChannelId(), RecipientIdentityId, SenderIdentityId, SenderDeviceId, ReceivedAtUtc);
