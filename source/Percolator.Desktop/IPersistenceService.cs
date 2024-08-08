using Google.Protobuf;

namespace Percolator.Desktop;

public interface IPersistenceService
{
    Task SetPreferredNickname(ByteString identity,string nickname);
}