using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

/// <summary>
/// Represents the group public parameters for ZK group operations.
/// Variable length depending on the group configuration.
/// </summary>
[ByteArray(minLength: 1, maxLength: 4096)]
public sealed partial record ZkGroupPublicParamsBytes;
