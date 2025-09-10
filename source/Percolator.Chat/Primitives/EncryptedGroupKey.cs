namespace Percolator.Chat.Primitives
{
    public sealed record EncryptedGroupKey(byte[] Value) : ByteArrayRecord(Value);
}
