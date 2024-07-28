using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;

namespace Percolator.Grpc.Services;

public class PersistenceService
{
    public ConcurrentDictionary<ByteString,DateTimeOffset> PendingKeys { get; } = new();
    public ConcurrentBag<ByteString> BannedKeys { get; } = new();
    public ConcurrentDictionary<ByteString, Aes> KnownSessions { get; } = new();
}