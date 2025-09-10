namespace Percolator.Chat.Primitives
{
    // Represents a group avatar image as a byte array record (opaque blob at domain boundary)
    public sealed record GroupAvatar(byte[] Value) : ByteArrayRecord(Value);
}
