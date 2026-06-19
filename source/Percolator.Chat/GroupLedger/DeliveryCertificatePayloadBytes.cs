using Percolator.SourceGenerators;

namespace Percolator.Chat.GroupLedger;

[ByteArray(minLength: 1, maxLength: 2048)]
public sealed partial record DeliveryCertificatePayloadBytes;
