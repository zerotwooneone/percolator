using Percolator.Cryptography;
using Percolator.SourceGenerators;

namespace Percolator.Chat;

[ByteArray(minLength: 1, maxLength: 2048)]
public sealed partial record DeliveryCertificatePayloadBytes;
