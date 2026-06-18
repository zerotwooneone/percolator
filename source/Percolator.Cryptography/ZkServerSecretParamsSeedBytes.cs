using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

/// <summary>
/// Represents the 32-byte randomness seed for generating server secret parameters.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record ZkServerSecretParamsSeedBytes;
