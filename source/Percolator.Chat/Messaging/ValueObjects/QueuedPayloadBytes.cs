using Percolator.SourceGenerators;

namespace Percolator.Chat.Messaging.ValueObjects;

[ByteArray(minLength: 1, maxLength: 1000000)]
public sealed partial record QueuedPayloadBytes;
