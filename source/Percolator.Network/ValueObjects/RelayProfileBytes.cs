using Percolator.SourceGenerators;

namespace Percolator.Network.ValueObjects;

/// <summary>
/// Encrypted group profile bytes stored on the Relay.
/// </summary>
[ByteArray(minLength: 1, maxLength: 5000)]
public sealed partial record RelayProfileBytes;
