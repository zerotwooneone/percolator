using Percolator.SourceGenerators;

namespace Percolator.Chat.Messaging.ValueObjects;

// SPKI-encoded identity public key bytes
[ByteArray(length:32)]
public sealed partial record IdentityPublicKey;