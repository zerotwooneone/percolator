using Percolator.SourceGenerators;

namespace Percolator.Identity;

[ByteArray(minLength: 1, maxLength: 1024)]
public sealed partial record EncryptedProfileDataBytes;
