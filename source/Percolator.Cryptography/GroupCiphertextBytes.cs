using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

/// <summary>
/// Represents encrypted group message ciphertext.
/// Variable length with reasonable bounds for group message payloads.
/// </summary>
[ByteArray(minLength: 1, maxLength: 4096)]
public sealed partial record GroupCiphertextBytes;
