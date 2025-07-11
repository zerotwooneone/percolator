using Percolator.Chat.Primitives;

namespace Percolator.Chat.ValueObjects;

public record ChannelId(byte[] Value) : ByteArrayRecord(Value);