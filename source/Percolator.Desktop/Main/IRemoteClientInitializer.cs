using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public interface IRemoteClientInitializer
{
    IReadOnlyDictionary<ByteString, RemoteClientModel> RemoteClients { get; }
    void AddKnownAnnouncers(IEnumerable<RemoteClientModel> announcerModels);
}