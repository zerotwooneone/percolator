namespace Percolator.Apps.Chat.Models;

public sealed record ChatPayloadEnvelopeDto(
    ChatPayloadType Type,
    ReadOnlyMemory<byte> Data,
    ReadOnlyMemory<byte> Padding);
