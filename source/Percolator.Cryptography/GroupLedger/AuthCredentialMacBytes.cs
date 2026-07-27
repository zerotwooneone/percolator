using Percolator.SourceGenerators;

namespace Percolator.Cryptography.GroupLedger;

[ByteArray(minLength: 1, maxLength: 1000)]
public sealed partial record AuthCredentialMacBytes;
