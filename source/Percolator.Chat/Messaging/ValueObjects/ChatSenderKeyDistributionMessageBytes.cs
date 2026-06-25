using Percolator.SourceGenerators;

namespace Percolator.Chat.Messaging.ValueObjects;

[ByteArray(minLength: 1, maxLength: 5000)]
public sealed partial record ChatSenderKeyDistributionMessageBytes;
