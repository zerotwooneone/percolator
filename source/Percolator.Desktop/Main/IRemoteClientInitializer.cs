using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public interface IRemoteClientInitializer
{
    Observable<ByteString> ClientAdded { get; }
    IReadOnlyDictionary<ByteString, RemoteClientModel> RemoteClients { get; }
    void AddKnownAnnouncers(IEnumerable<RemoteClientModel> announcerModels);
}