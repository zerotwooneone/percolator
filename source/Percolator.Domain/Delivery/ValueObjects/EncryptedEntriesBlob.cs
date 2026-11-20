using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

[ByteArray(minLength: 1, maxLength: 5000)]
public sealed partial record EncryptedEntriesBlob;
