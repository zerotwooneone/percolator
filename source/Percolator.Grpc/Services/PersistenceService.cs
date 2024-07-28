using System.Collections.Concurrent;
using Google.Protobuf;

namespace Percolator.Grpc.Services;

public class PersistenceService
{
    public ConcurrentBag<ByteString> KnownKeys { get; } = new();
    public ConcurrentDictionary<ByteString,DateTimeOffset> PendingKeys { get; } = new();
    public ConcurrentBag<ByteString> BannedKeys { get; } = new();
}