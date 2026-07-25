using Percolator.SourceGenerators;

namespace Percolator.Network.ValueObjects;

/// <summary>
/// Public parameters for a Relay group (group public key).
/// </summary>
[ByteArray(minLength: 1, maxLength: 5000)]
public sealed partial record RelayGroupPublicParamsBytes;
