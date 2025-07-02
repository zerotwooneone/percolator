namespace Percolator.Network;

/// <summary>
/// A DDD value type representing a serialized data payload to be signed or verified.
/// </summary>
public record Payload(byte[] Value);
