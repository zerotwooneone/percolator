using System.Diagnostics;

namespace Percolator.Cryptography.Primitives;

/// <summary>
/// Represents a unique identifier for a peer within the cryptographic domain.
/// This is a stable identifier that is not tied to a specific cryptographic key.
/// </summary>
[DebuggerDisplay("{Value}")]
public readonly record struct PeerId(uint Value)
{
    public override string ToString() => Value.ToString();
}
