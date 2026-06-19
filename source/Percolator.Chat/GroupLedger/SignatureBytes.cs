using Percolator.SourceGenerators;

namespace Percolator.Chat.GroupLedger;

/// <summary>
/// Chat-native signature bytes. Maps to Percolator.Cryptography.Signature at the Application layer boundary.
/// </summary>
[ByteArray(minLength: 64, maxLength: 64)]
public sealed partial record SignatureBytes;
