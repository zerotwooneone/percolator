using Percolator.Network.Primitives;

namespace Percolator.Network;

/// <summary>
/// Represents the public key required to initiate a direct message session with a peer.
/// This is a value object.
/// </summary>
public record DirectMessagePublicKey(byte[] Value) : ByteArrayRecord(Value);
