using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

[ByteArray(minLength: 1, maxLength: 1000)]
public sealed partial record AuthCredentialMacBytes;
