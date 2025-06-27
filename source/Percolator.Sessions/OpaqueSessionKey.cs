namespace Percolator.Sessions;

/// <summary>
/// Represents a derived cryptographic session key.
/// </summary>
public record OpaqueSessionKey(byte[] Value);
