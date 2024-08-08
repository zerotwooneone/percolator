using Google.Protobuf;

namespace Percolator.Desktop;

public interface IPersistenceService
{
    Task UpdateAnnouncer(ByteString identity, string nickname, DateTimeOffset lastSeenValue);
}