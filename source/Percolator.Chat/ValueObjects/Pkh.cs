using Percolator.Chat.Primitives;

namespace Percolator.Chat.ValueObjects;

public sealed record Pkh(byte[] Value) : ByteArrayRecord(Value)
{
    public static Pkh FromBytes(byte[] value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        return new Pkh(value);
    }
}
