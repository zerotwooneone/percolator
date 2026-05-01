using Percolator.SourceGenerators;

namespace Percolator.Network;

/// <summary>
/// Represents the public key required to initiate a direct message session with a peer.
/// This is a value object.
/// </summary>
[ByteArray(minLength: 80, maxLength: 200)]
public sealed partial record DirectMessagePublicKey;
