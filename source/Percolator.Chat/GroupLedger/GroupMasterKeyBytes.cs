using Percolator.SourceGenerators;

namespace Percolator.Chat.GroupLedger;

/// <summary>
/// Chat-native group master key bytes. Maps to Percolator.Cryptography.GroupMasterKey at the Application layer boundary.
/// </summary>
[ByteArray(minLength: 32, maxLength: 32)]
public sealed partial record GroupMasterKeyBytes;
