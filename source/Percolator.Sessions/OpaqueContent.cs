namespace Percolator.Sessions;

/// <summary>
/// Represents an opaque block of data, ensuring that raw byte arrays are not passed across domain boundaries.
/// This value type wraps the encrypted payload of a message.
/// </summary>
public record OpaqueContent
{
    public byte[] Value { get; }

    public OpaqueContent(byte[] value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    // Implicit conversion can be useful for convenience, but let's be explicit for now.
    // public static implicit operator byte[](OpaqueContent content) => content.Value;
    // public static implicit operator OpaqueContent(byte[] value) => new(value);
}
