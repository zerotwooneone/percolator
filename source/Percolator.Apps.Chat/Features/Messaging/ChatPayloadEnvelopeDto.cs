namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record ChatPayloadEnvelopeDto(
    ChatPayloadType Type,
    ReadOnlyMemory<byte> Data,
    ReadOnlyMemory<byte> Padding);
